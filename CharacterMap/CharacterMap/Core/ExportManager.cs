﻿using CharacterMap.Helpers;
using CharacterMap.Services;
using CharacterMapCX;
using GalaSoft.MvvmLight.Ioc;
using GalaSoft.MvvmLight.Views;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Svg;
using Microsoft.Graphics.Canvas.Text;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.Xaml.Markup;
using Windows.UI.Xaml.Media;
using CharacterMap.Models;
using System.IO;
using System.IO.Compression;
using GalaSoft.MvvmLight.Messaging;

namespace CharacterMap.Core
{
    public enum ExportStyle
    {
        Black,
        White,
        ColorGlyph
    }

    public class ExportResult
    {
        public StorageFile File { get; }
        public bool Success { get; }

        public ExportResult(bool success, StorageFile file)
        {
            Success = success;
            File = file;
        }
    }

    public class ExportFontFileResult
    {
        public StorageFolder Folder { get; }
        public StorageFile File { get; }
        public bool Success { get; }

        public ExportFontFileResult(bool success, StorageFile file)
        {
            Success = success;
            File = file;
        }

        public ExportFontFileResult(bool success, StorageFolder folder)
        {
            Success = success;
            Folder = folder;
        }

        public string GetMessage()
        {
            return Localization.Get("FontExportedMessage", File?.Name ?? Folder?.Name);
        }
    }

    public static class ExportManager
    {
        public static async void RequestExportFontFile(FontVariant variant, CanvasTextLayoutAnalysis ana)
        {
            string name = Path.GetFileNameWithoutExtension(ana.FilePath);
            string ext = Path.GetExtension(ana.FilePath);
            if (await PickFileAsync(name, Localization.Get("ExportFontFile/Text"), new[] { ext }, PickerLocationId.DocumentsLibrary) is StorageFile file)
            {
                try
                {
                    var interop = SimpleIoc.Default.GetInstance<Interop>();
                    bool success = await interop.WriteToFileAsync(variant.FontFace, file);
                    Messenger.Default.Send(new AppNotificationMessage(true, new ExportFontFileResult(success, file)));
                }
                catch
                {
                    Messenger.Default.Send(new AppNotificationMessage(true, new ExportFontFileResult(false, file)));
                }
            }
        }

        public static async Task<ExportResult> ExportSvgAsync(
            ExportStyle style,
            InstalledFont selectedFont,
            FontVariant selectedVariant,
            Character selectedChar,
            CanvasTextLayoutAnalysis analysis,
            CanvasTypography typography)
        {
            try
            {
                string name = GetFileName(selectedFont, selectedVariant, selectedChar, "svg");
                if (await PickFileAsync(name, "SVG", new[] { ".svg" }) is StorageFile file)
                {
                    CachedFileManager.DeferUpdates(file);
                   
                    CanvasDevice device = Utils.CanvasDevice;
                    Color textColor = style == ExportStyle.Black ? Colors.Black : Colors.White;


                    // If COLR format (e.g. Segoe UI Emoji), we have special export path.
                    if (style == ExportStyle.ColorGlyph && analysis.HasColorGlyphs && !analysis.GlyphFormats.Contains(GlyphImageFormat.Svg))
                    {
                        Interop interop = SimpleIoc.Default.GetInstance<Interop>();
                        List<string> paths = new List<string>();
                        Rect bounds = Rect.Empty;

                        foreach (var thing in analysis.Indicies)
                        {
                            var path = interop.GetPathDatas(selectedVariant.FontFace, thing.ToArray()).First();
                            paths.Add(path.Path);

                            if (!path.Bounds.IsEmpty)
                            {
                                var left = Math.Min(bounds.Left, path.Bounds.Left);
                                var top = Math.Min(bounds.Top, path.Bounds.Top);
                                var right = Math.Max(bounds.Right, path.Bounds.Right);
                                var bottom = Math.Max(bounds.Bottom, path.Bounds.Bottom);
                                bounds = new Rect(
                                    left,
                                    top,
                                    right - left,
                                    bottom - top);
                            }
                        }

                        using (CanvasSvgDocument document = Utils.GenerateSvgDocument(device, bounds, paths, analysis.Colors, invertBounds: false))
                        {
                            await Utils.WriteSvgAsync(document, file);
                        }

                        return new ExportResult(true, file);
                    }





                    var data = GetGeometry(1024, selectedVariant, selectedChar, analysis, typography);
                    async Task SaveMonochromeAsync()
                    {
                        using (CanvasSvgDocument document = Utils.GenerateSvgDocument(device, data.Bounds, data.Path, textColor))
                        {
                            await Utils.WriteSvgAsync(document, file);
                        }
                    }

                    // If the font uses SVG glyphs, we can extract the raw SVG from the font file
                    if (analysis.GlyphFormats.Contains(GlyphImageFormat.Svg))
                    {
                        byte[] bytes = GetGlyphBytes(selectedVariant.FontFace, selectedChar.UnicodeIndex, 8);
                        
                        string str;

                        if (bytes.Length > 2 && bytes[0] == 31 && bytes[1] == 139)
                        {
                            // Content is GZIP'd. Decompress first.
                            using (var stream = new MemoryStream(bytes))
                            using (var gzip = new GZipStream(stream, CompressionMode.Decompress))
                            using (var reader = new StreamReader(gzip))
                            {
                                str = reader.ReadToEnd();
                            }
                        }
                        else
                        {
                            str = Encoding.UTF8.GetString(bytes);
                        }

                        if (str.StartsWith("<?xml"))
                            str = str.Remove(0, str.IndexOf(">") + 1);

                        str = str.TrimStart();

                        try
                        {
                            using (CanvasSvgDocument document = CanvasSvgDocument.LoadFromXml(Utils.CanvasDevice, str))
                            {
                                // We need to transform the SVG to fit within the default document bounds, as characters
                                // are based *above* the base origin of (0,0) as (0,0) is the Baseline (bottom left) position for a character, 
                                // so by default a will appear out of bounds of the default SVG viewport (towards top left).

                                //if (!document.Root.IsAttributeSpecified("viewBox")) // Specified viewbox requires baseline transform?
                                {
                                    // We'll regroup all the elements inside a "g" / group tag,
                                    // and apply a transform to the "g" tag to try and put in 
                                    // in the correct place. There's probably a more accurate way
                                    // to do this by directly setting the root viewBox, if anyone
                                    // can find the correct calculation...

                                    List<ICanvasSvgElement> elements = new List<ICanvasSvgElement>();

                                    double minTop = 0;
                                    double minLeft = double.MaxValue;
                                    double maxWidth = double.MinValue;
                                    double maxHeight = double.MinValue;

                                    void ProcessChildren(CanvasSvgNamedElement root)
                                    {
                                        CanvasSvgNamedElement ele = root.FirstChild as CanvasSvgNamedElement;
                                        while (true)
                                        {
                                            CanvasSvgNamedElement next = root.GetNextSibling(ele) as CanvasSvgNamedElement;
                                            if (ele.Tag == "g")
                                            {
                                                ProcessChildren(ele);
                                            }
                                            else if (ele.Tag == "path")
                                            {
                                                // Create a XAML geometry to try and find the bounds of each character
                                                // Probably more efficient to do in Win2D, but far less code to do with XAML.
                                                Geometry gm = XamlBindingHelper.ConvertValue(typeof(Geometry), ele.GetStringAttribute("d")) as Geometry;
                                                minTop = Math.Min(minTop, gm.Bounds.Top);
                                                minLeft = Math.Min(minLeft, gm.Bounds.Left);
                                                maxWidth = Math.Max(maxWidth, gm.Bounds.Width);
                                                maxHeight = Math.Max(maxHeight, gm.Bounds.Height);
                                            }
                                            ele = next;
                                            if (ele == null)
                                                break;
                                        }
                                    }

                                    ProcessChildren(document.Root);

                                    double top = minTop < 0 ? minTop : 0;
                                    double left = minLeft;
                                    document.Root.SetRectangleAttribute("viewBox", new Rect(left, top, data.Bounds.Width, data.Bounds.Height));
                                }

                                await Utils.WriteSvgAsync(document, file);
                            }
                        }
                        catch
                        {
                            // Certain fonts seem to have their SVG glyphs encoded with... I don't even know what encoding.
                            // for example: https://github.com/adobe-fonts/emojione-color
                            // In these cases, fallback to monochrome black
                            await SaveMonochromeAsync();
                        }
                    }
                    else
                    {
                        await SaveMonochromeAsync();
                    }

                    await CachedFileManager.CompleteUpdatesAsync(file);
                    return new ExportResult(true, file);
                }
            }
            catch (Exception ex)
            {
                await SimpleIoc.Default.GetInstance<IDialogService>()
                    .ShowMessageBox(ex.Message, Localization.Get("SaveImageError"));
            }

            return new ExportResult(false, null);
        }

        public static async Task<ExportResult> ExportPngAsync(
            ExportStyle style,
            InstalledFont selectedFont,
            FontVariant selectedVariant,
            Character selectedChar,
            CanvasTextLayoutAnalysis analysis,
            CanvasTypography typography,
            AppSettings settings)
        {
            try
            {
                string name = GetFileName(selectedFont, selectedVariant, selectedChar, "png");
                if (await PickFileAsync(name, "PNG Image", new[] { ".png" }) is StorageFile file)
                {
                    CachedFileManager.DeferUpdates(file);

                    if (analysis.GlyphFormats.Contains(GlyphImageFormat.Png))
                    {
                        byte[] bytes = GetGlyphBytes(selectedVariant.FontFace, selectedChar.UnicodeIndex, 16);
                        await FileIO.WriteBytesAsync(file, bytes);
                    }
                    else
                    {
                        var device = Utils.CanvasDevice;
                        var localDpi = 96; //Windows.Graphics.Display.DisplayInformation.GetForCurrentView().LogicalDpi;

                        var canvasH = (float)settings.PngSize;
                        var canvasW = (float)settings.PngSize;

                        using (var renderTarget = new CanvasRenderTarget(device, canvasW, canvasH, localDpi))
                        {
                            using (var ds = renderTarget.CreateDrawingSession())
                            {
                                ds.Clear(Colors.Transparent);
                                var d = settings.PngSize;
                                var r = settings.PngSize / 2;

                                var textColor = style == ExportStyle.Black ? Colors.Black : Colors.White;
                                var fontSize = (float)d;

                                using (CanvasTextLayout layout = new CanvasTextLayout(device, $"{selectedChar.Char}", new CanvasTextFormat
                                {
                                    FontSize = fontSize,
                                    FontFamily = selectedVariant.Source,
                                    FontStretch = selectedVariant.FontFace.Stretch,
                                    FontWeight = selectedVariant.FontFace.Weight,
                                    FontStyle = selectedVariant.FontFace.Style,
                                    HorizontalAlignment = CanvasHorizontalAlignment.Center,
                                    Options = style == ExportStyle.ColorGlyph ? CanvasDrawTextOptions.EnableColorFont : CanvasDrawTextOptions.Default
                                }, canvasW, canvasH))
                                {
                                    if (style == ExportStyle.ColorGlyph)
                                        layout.Options = CanvasDrawTextOptions.EnableColorFont;

                                    layout.SetTypography(0, 1, typography);

                                    var db = layout.DrawBounds;
                                    double scale = Math.Min(1, Math.Min(canvasW / db.Width, canvasH / db.Height));
                                    var x = -db.Left + ((canvasW - (db.Width * scale)) / 2d);
                                    var y = -db.Top + ((canvasH - (db.Height * scale)) / 2d);

                                    ds.Transform =
                                        Matrix3x2.CreateTranslation(new Vector2((float)x, (float)y))
                                        * Matrix3x2.CreateScale(new Vector2((float)scale));

                                    ds.DrawTextLayout(layout, new Vector2(0), textColor);
                                }
                            }

                            using (var fileStream = await file.OpenAsync(FileAccessMode.ReadWrite))
                            {
                                fileStream.Size = 0;
                                await renderTarget.SaveAsync(fileStream, CanvasBitmapFileFormat.Png, 1f);
                            }
                        }
                    }

                    await CachedFileManager.CompleteUpdatesAsync(file);
                    return new ExportResult(true, file);
                }
            }
            catch (Exception ex)
            {
                await SimpleIoc.Default.GetInstance<IDialogService>()
                    .ShowMessageBox(ex.Message, Localization.Get("SaveImageError"));
            }

            return new ExportResult(false, null);
        }

        private static byte[] GetGlyphBytes(CanvasFontFace fontface, int unicodeIndex, int imageType)
        {
            Interop interop = SimpleIoc.Default.GetInstance<Interop>();
            IBuffer buffer = interop.GetImageDataBuffer(fontface, 1024, (uint)unicodeIndex, (uint)imageType);
            using (DataReader reader = DataReader.FromBuffer(buffer))
            {
                byte[] bytes = new byte[buffer.Length];
                reader.ReadBytes(bytes);
                return bytes;
            }
        }

        private static string GetFileName(
            InstalledFont selectedFont,
            FontVariant selectedVariant,
            Character selectedChar,
            string ext)
        {
            var chr = GlyphService.GetCharacterDescription(selectedChar.UnicodeIndex, selectedVariant) ?? selectedChar.UnicodeString;
            return $"{selectedFont.Name} {selectedVariant.PreferredName} - {chr}.{ext}";
        }

        private static async Task<StorageFile> PickFileAsync(string fileName, string key, IList<string> values, PickerLocationId suggestedLocation = PickerLocationId.PicturesLibrary)
        {
            var savePicker = new FileSavePicker
            {
                SuggestedStartLocation = suggestedLocation
            };

            savePicker.FileTypeChoices.Add(key, values);
            savePicker.SuggestedFileName = fileName;

            try
            {
                return await savePicker.PickSaveFileAsync();
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static (string Path, Rect Bounds) GetGeometry(
            float size,
            FontVariant selectedVariant,
            Character selectedChar,
            CanvasTextLayoutAnalysis analysis,
            CanvasTypography typography)
        {
            using (CanvasGeometry geom = CreateGeometry(size, selectedVariant, selectedChar, analysis, typography))
            {
                /* 
                 * Unfortunately this only constructs a monochrome path, if we want color
                 * Win2D does not yet expose the necessary API's to get the individual glyph
                 * layers that make up a color glyph.
                 * 
                 * We'll need to handle this in C++/CX if we want to do this at some point.
                 */

                var bounds = geom.ComputeBounds();
                var interop = SimpleIoc.Default.GetInstance<Interop>();
                var s = interop.GetPathData(geom);

                var t = s.Transform.Translation;
                bounds = new Rect(t.X - bounds.Left, -bounds.Top + t.Y, bounds.Width, bounds.Height);
                return (s.Path, bounds);
            }
        }

        public static CanvasGeometry CreateGeometry(
           float size,
           FontVariant selectedVariant,
           Character selectedChar,
           CanvasTextLayoutAnalysis analysis,
           CanvasTypography typography)
        {
            CanvasDevice device = Utils.CanvasDevice;

            /* SVG Exports render at fixed size - but a) they're vectors, and b) they're
             * inside an auto-scaling viewport. So render-size is *largely* pointless */
            float canvasH = size, canvasW = size, fontSize = size;

            using (CanvasTextLayout layout = new CanvasTextLayout(device, $"{selectedChar.Char}", new CanvasTextFormat
            {
                FontSize = fontSize,
                FontFamily = selectedVariant.Source,
                FontStretch = selectedVariant.FontFace.Stretch,
                FontWeight = selectedVariant.FontFace.Weight,
                FontStyle = selectedVariant.FontFace.Style,
                HorizontalAlignment = CanvasHorizontalAlignment.Center
            }, canvasW, canvasH))
            {
                layout.SetTypography(0, 1, typography);
                layout.Options = analysis.GlyphFormats.Contains(GlyphImageFormat.Svg) ? CanvasDrawTextOptions.EnableColorFont : CanvasDrawTextOptions.Default;

                return CanvasGeometry.CreateText(layout);
            }
        }

        internal static async Task ExportCollectionAsZipAsync(List<InstalledFont> fontList, UserFontCollection selectedCollection)
        {
            var fonts = fontList.SelectMany(f => f.Variants).ToList();

            if (await PickFileAsync(selectedCollection.Name, "ZIP", new[] { ".zip" }) is StorageFile file)
            {
                await Task.Run(async () =>
                {
                    var interop = SimpleIoc.Default.GetInstance<Interop>();
                    using var i = await file.OpenStreamForWriteAsync();
                    i.SetLength(0);
                    using ZipArchive z = new ZipArchive(i, ZipArchiveMode.Create);
                    foreach (var font in fonts)
                    {
                        if (interop.GetFileBuffer(font.FontFace) is FontFileData d)
                        {
                            ZipArchiveEntry entry = z.CreateEntry(CleanFileName(font, d.FileName));
                            using (IOutputStream s = entry.Open().AsOutputStream())
                            {
                                DataWriter w = new DataWriter(s);
                                w.WriteBuffer(d.Buffer);
                                await w.StoreAsync();
                                await w.FlushAsync();
                            }

                            d.Dispose();
                        }
                    }
                });

                Messenger.Default.Send(new AppNotificationMessage(true, new ExportFontFileResult(true, file)));
            }
        }

        internal static async Task ExportCollectionToFolderAsync(List<InstalledFont> fontList, UserFontCollection selectedCollection)
        {
            var fonts = fontList.SelectMany(f => f.Variants).ToList();

            FolderPicker picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeFilter.Add("*");


            if (await picker.PickSingleFolderAsync() is StorageFolder folder)
            {
                await Task.Run(async () =>
                {
                    var interop = SimpleIoc.Default.GetInstance<Interop>();
                    foreach (var font in fonts)
                    {
                        if (interop.GetFileBuffer(font.FontFace) is FontFileData d)
                        {
                            StorageFile file = await folder.CreateFileAsync(CleanFileName(font, d.FileName), CreationCollisionOption.ReplaceExisting);
                            
                            using var stream = await file.OpenStreamForWriteAsync();
                            stream.SetLength(0);

                            DataWriter w = new DataWriter(stream.AsOutputStream());
                            w.WriteBuffer(d.Buffer);
                            await w.StoreAsync();
                            await w.FlushAsync();

                            d.Dispose();
                        }
                    }
                });

                Messenger.Default.Send(new AppNotificationMessage(true, new ExportFontFileResult(true, folder)));
            }
        }

        private static string CleanFileName(FontVariant font, string fileName)
        {
            fileName = fileName ?? $"{font.FamilyName} {font.PreferredName}.ttf";
            return $"{Humanizer.To.SentenceCase.Transform(Path.GetFileNameWithoutExtension(fileName))}{Path.GetExtension(fileName).ToLower()}";
        }

    }
}
