﻿using CharacterMap.Controls;
using CharacterMap.Core;
using CharacterMap.Helpers;
using CharacterMap.Models;
using CharacterMap.Services;
using CharacterMap.ViewModels;
using CharacterMap.Views;
using CharacterMapCX;
using CommonServiceLocator;
using GalaSoft.MvvmLight.Messaging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace CharacterMap.Helpers
{
    public static class FlyoutHelper
    {
        private static UserCollectionsService _collections { get; } = ServiceLocator.Current.GetInstance<UserCollectionsService>();

        public static void RequestDelete(InstalledFont font)
        {
            MainViewModel main = ResourceHelper.Get<ViewModelLocator>("Locator").Main;
            var d = new ContentDialog
            {
                Title = Localization.Get("DlgDeleteFont/Title"),
                IsPrimaryButtonEnabled = true,
                IsSecondaryButtonEnabled = true,
                PrimaryButtonText = Localization.Get("DigDeleteCollection/PrimaryButtonText"),
                SecondaryButtonText = Localization.Get("DigDeleteCollection/SecondaryButtonText"),
            };

            d.PrimaryButtonClick += (ds, de) =>
            {
                _ = MainPage.MainDispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    main.TryRemoveFont(font);
                });
            };
            _ = d.ShowAsync();
        }

        

        public static void CreateMenu(
            MenuFlyout menu,
            InstalledFont font,
            FontVariant variant,
            CanvasTextLayoutAnalysis variantAnalysis,
            bool standalone,
            bool showAdvanced = false)
        {
            MainViewModel main = ResourceHelper.Get<ViewModelLocator>("Locator").Main;

            #region Handlers 

            static void OpenInNewWindow(object s, RoutedEventArgs args)
            {
                if (s is FrameworkElement f && f.Tag is InstalledFont fnt)
                    _ = FontMapView.CreateNewViewForFontAsync(fnt);
            }

            static async void AddToSymbolFonts_Click(object sender, RoutedEventArgs e)
            {
                if (sender is FrameworkElement f && f.DataContext is InstalledFont fnt)
                {
                    var result = await _collections.AddToCollectionAsync(fnt, _collections.SymbolCollection);

                    Messenger.Default.Send(new CollectionsUpdatedMessage());

                    if (result.Success)
                        Messenger.Default.Send(new AppNotificationMessage(true, result));
                }
            }

            static void CreateCollection_Click(object sender, RoutedEventArgs e)
            {
                var d = new CreateCollectionDialog
                {
                    DataContext = (sender as FrameworkElement)?.DataContext
                };

                _ = d.ShowAsync();
            }

            static void SaveFont_Click(object sender, RoutedEventArgs e)
            {
                if (sender is MenuFlyoutItem item && item.Tag is (FontVariant fnt, CanvasTextLayoutAnalysis ana))
                {
                    ExportManager.RequestExportFontFile(fnt, ana);
                }
            }

            static void Print_Click(object sender, RoutedEventArgs e)
            {
                Messenger.Default.Send(new PrintRequestedMessage());
            }

            async void RemoveFrom_Click(object sender, RoutedEventArgs e)
            {
                if (sender is FrameworkElement f && f.DataContext is InstalledFont fnt)
                {

                    UserFontCollection collection = (main.SelectedCollection == null && main.FontListFilter == 1)
                        ? _collections.SymbolCollection
                        : main.SelectedCollection;

                    await _collections.RemoveFromCollectionAsync(fnt, collection);
                    Messenger.Default.Send(new AppNotificationMessage(true, new CollectionUpdatedArgs(fnt, collection, false)));
                    Messenger.Default.Send(new CollectionsUpdatedMessage());
                }
            }

            static void DeleteMenuFlyoutItem_Click(object sender, RoutedEventArgs e)
            {
                if (sender is MenuFlyoutItem item && item.Tag is InstalledFont fnt)
                {
                    RequestDelete(fnt);
                }
            }

            #endregion


            if (menu.Items != null)
            {
                menu.Items.Clear();
                MenuFlyoutSubItem coll;

                {
                    // HORRIBLE Hacks, because MenuFlyoutSubItem never updates it's UI tree after the first
                    // render, meaning we can't dynamically update items. Instead we need to make an entirely
                    // new one.

                    // Add "Open in New Window" button
                    if (!standalone)
                    {
                        var newWindow = new MenuFlyoutItem
                        {
                            Text = Localization.Get("OpenInNewWindow/Text"),
                            Icon = new SymbolIcon {Symbol = Symbol.NewWindow},
                            Tag = font
                        };
                        newWindow.Click += OpenInNewWindow;
                        menu.Items.Add(newWindow);

                        if (showAdvanced)
                        {
                            newWindow.AddKeyboardAccelerator(VirtualKey.N, VirtualKeyModifiers.Control);
                        }
                    }

                    if (variant != null && variantAnalysis != null && !string.IsNullOrWhiteSpace(variantAnalysis.FilePath))
                    {
                        var saveButton = new MenuFlyoutItem
                        {
                            Text = Localization.Get("ExportFontFileLabel/Text"),
                            Icon = new FontIcon { Glyph = "\uE792" },
                            Tag = (variant, variantAnalysis)
                        }.AddKeyboardAccelerator(VirtualKey.S, VirtualKeyModifiers.Control);

                        saveButton.Click += SaveFont_Click;
                        menu.Items.Add(saveButton);
                    }

                    // Add "Add to Collection" button
                    MenuFlyoutSubItem newColl = new MenuFlyoutSubItem
                    {
                        Text = Localization.Get("AddToCollectionFlyout/Text"),
                        Icon = new SymbolIcon {Symbol = Symbol.AllApps}
                    };

                    // Create "New Collection" Item
                    var newCollection = new MenuFlyoutItem
                    {
                        Text = Localization.Get("NewCollectionItem/Text"),
                        Icon = new SymbolIcon
                        {
                            Symbol = Symbol.Add
                        }
                    };
                    newCollection.Click += CreateCollection_Click;
                    if (newColl.Items != null)
                    {
                        newColl.Items.Add(newCollection);

                        // Create "Symbol Font" item
                        if (!font.IsSymbolFont)
                        {
                            newColl.Items.Add(new MenuFlyoutSeparator());

                            var symb = new MenuFlyoutItem
                            {
                                Text = Localization.Get("OptionSymbolFonts/Text"),
                                IsEnabled = !_collections.SymbolCollection.Fonts.Contains(font.Name)
                            };
                            symb.Click += AddToSymbolFonts_Click;
                            newColl.Items.Add(symb);
                        }
                    }

                    coll = newColl;
                    menu.Items.Add(coll);
                }

                // Add items for each user Collection
                if (_collections.Items.Count > 0)
                {
                    if (coll.Items != null)
                    {
                        coll.Items.Add(new MenuFlyoutSeparator());

                        foreach (var m in
                                _collections.Items.Select(item => new MenuFlyoutItem
                                {
                                    DataContext = item,
                                    Text = item.Name,
                                    IsEnabled = !item.Fonts.Contains(font.Name)
                                }))
                        {
                            if (m.IsEnabled)
                            {
                                m.Click += async (s, a) =>
                                {
                                    UserFontCollection collection =
                                        (UserFontCollection)((FrameworkElement)s).DataContext;
                                    AddToCollectionResult result =
                                        await _collections.AddToCollectionAsync(font, collection);

                                    if (result.Success)
                                    {
                                        Messenger.Default.Send(new AppNotificationMessage(true, result));
                                    }
                                };
                            }

                            coll.Items.Add(m);
                        }
                    }
                }

                // Only show the "Remove from Collection" menu item if:
                //  -- we are not in a standalone window
                //  AND
                //  -- we are in a custom collection
                //  OR 
                //  -- we are in the Symbol Font collection, and this is a font that 
                //     the user has manually tagged as a symbol font
                if (!standalone)
                {
                    if (main.SelectedCollection != null ||
                        (main.FontListFilter == 1 && !font.FontFace.IsSymbolFont))
                    {
                        menu.Items.Add(new MenuFlyoutSeparator());

                        var removeItem = new MenuFlyoutItem
                        {
                            Text = Localization.Get("RemoveFromCollectionItem/Text"),
                            Icon = new SymbolIcon {Symbol = Symbol.Remove},
                            Tag = font
                        };
                        removeItem.Click += RemoveFrom_Click;
                        menu.Items.Add(removeItem);
                    }
                }
                if (showAdvanced)
                {
                    if (Windows.Graphics.Printing.PrintManager.IsSupported())
                    {
                        MenuFlyoutItem item = new MenuFlyoutItem
                        {
                            Text = Localization.Get("BtnPrint/Content"),
                            Icon = new SymbolIcon { Symbol = Symbol.Print }
                        }.AddKeyboardAccelerator(VirtualKey.P, VirtualKeyModifiers.Control);

                        item.Click += Print_Click;
                        menu.Items.Insert(standalone ? 0 : 1, item);
                    }
                }

                // Add "Delete Font" button
                if (!standalone)
                {
                    if (font.HasImportedFiles)
                    {
                        menu.Items.Add(new MenuFlyoutSeparator());

                        var removeFont = new MenuFlyoutItem
                        {
                            Text = Localization.Get("RemoveFontFlyout/Text"),
                            Icon = new SymbolIcon { Symbol = Symbol.Delete },
                            Tag = font
                        };

                        if (showAdvanced)
                            removeFont.AddKeyboardAccelerator(VirtualKey.Delete, VirtualKeyModifiers.Control);

                        removeFont.Click += DeleteMenuFlyoutItem_Click;
                        menu.Items.Add(removeFont);
                    }
                }
            }
        }
    }
}
