﻿using ExClient;
using ExViewer.ViewModels;
using GalaSoft.MvvmLight.Ioc;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

// “空白页”项模板在 http://go.microsoft.com/fwlink/?LinkId=234238 上有介绍

namespace ExViewer.Views
{
    /// <summary>
    /// 可用于自身或导航至 Frame 内部的空白页。
    /// </summary>
    public sealed partial class SavedPage : Page, IHasAppBar
    {
        public SavedPage()
        {
            this.InitializeComponent();
            VM = SavedVM.Instance;
            cdg_ConfirmClear = new ContentDialog()
            {
                Title = LocalizedStrings.Resources.Views.ClearSavedDialog.Title,
                Content = LocalizedStrings.Resources.Views.ClearSavedDialog.Content,
                PrimaryButtonText = LocalizedStrings.Resources.OK,
                SecondaryButtonText = LocalizedStrings.Resources.Cancel,
                PrimaryButtonCommand = VM.Clear
            };
        }

        public SavedVM VM
        {
            get
            {
                return (SavedVM)GetValue(VMProperty);
            }
            set
            {
                SetValue(VMProperty, value);
            }
        }

        // Using a DependencyProperty as the backing store for VM.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty VMProperty =
            DependencyProperty.Register("VM", typeof(SavedVM), typeof(SavedPage), new PropertyMetadata(null));

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if(e.NavigationMode != NavigationMode.Back || VM.Galleries == null)
            {
                VM.Refresh.Execute(null);
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
        }

        private void btn_Pane_Click(object sender, RoutedEventArgs e)
        {
            RootControl.RootController.SwitchSplitView();
        }

        private void lv_ItemClick(object sender, ItemClickEventArgs e)
        {
            if(VM.Open.CanExecute(e.ClickedItem))
                VM.Open.Execute(e.ClickedItem);
        }

        private readonly ContentDialog cdg_ConfirmClear;

        private async void abb_DeleteAll_Click(object sender, RoutedEventArgs e)
        {
            cdg_ConfirmClear.RequestedTheme = Settings.SettingCollection.Current.Theme.ToElementTheme();
            await cdg_ConfirmClear.ShowAsync();
        }

        private void lv_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
        {
            var item = e.Items.FirstOrDefault() as Gallery;
            if(item == null)
            {
                e.Cancel = true;
                return;
            }
            FlyoutBase.GetAttachedFlyout((FrameworkElement)((ListViewItem)lv.ContainerFromItem(item)).ContentTemplateRoot).Hide();
            e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
            {
                var d = request.GetDeferral();
                try
                {
                    var makeCopy = SavedVM.GetCopyOf(item);
                    request.SetData(new IStorageItem[] { await makeCopy });
                }
                finally { d.Complete(); }
            });
            e.Data.Properties.ApplicationName = Package.Current.DisplayName;
            e.Data.Properties.PackageFamilyName = Package.Current.Id.FamilyName;
            e.Data.Properties.Description = item.Title;
            e.Data.Properties.Title = item.Title;
            e.Data.Properties.Thumbnail = RandomAccessStreamReference.CreateFromUri(item.ThumbUri);
            e.Data.RequestedOperation = DataPackageOperation.Move;
        }

        private void lv_RefreshRequested(object sender, EventArgs args)
        {
            VM.Refresh.Execute(null);
        }

        public void CloseAll()
        {
            cb_top.IsOpen = false;
        }

        private void lv_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            var lvi = (args.OriginalSource as DependencyObject)?.FirstAncestor<ListViewItem>();
            if(lvi == null)
                return;
            var dc = lvi.DataContext;
            mfi_DeleteGallery.CommandParameter = dc;
            mfi_SaveTo.CommandParameter = dc;
            mf_Gallery.ShowAt(lvi);
            args.Handled = true;
        }

        private void lv_ContextCanceled(UIElement sender, RoutedEventArgs args)
        {
            mf_Gallery.Hide();
        }
    }
}
