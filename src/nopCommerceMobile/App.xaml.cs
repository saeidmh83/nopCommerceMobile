﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using nopCommerceMobile.Models.Customer;
using nopCommerceMobile.Models.Localization;
using nopCommerceMobile.Services.Customer;
using nopCommerceMobile.Services.Localization;
using nopCommerceMobile.ViewModels;
using nopCommerceMobile.ViewModels.Base;
using SQLite;
using Xamarin.Forms;
using NavigationPage = nopCommerceMobile.Views.NavigationPage;

namespace nopCommerceMobile
{
    public partial class App : Application
    {

        #region Fields

        private ICustomerService _customerService;
        private ILocalizationService _localizationService;
        public static CustomerModel CurrentCostumer;
        public static IList<LocaleResourceModel> LocaleResources;
        public static string CustomerAppCulture { get; set; }

        #endregion

        #region Ctor

        public App()
        {
            InitializeComponent();

            if (_customerService == null && CurrentCostumer == null)
                _customerService = LocatorViewModel.Resolve<ICustomerService>();

            if (_localizationService == null)
                _localizationService = LocatorViewModel.Resolve<ILocalizationService>();

            InitApp();
        }

        #endregion

        private void InitApp()
        {
            InitializeDataBase(); //fix locale resources on first load, initialize database from service TODO
            MainPage = GetMainPage();
        }

        private async void InitializeDataBase()
        {
            var databasePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "nopCommerce.db");
            var database = new SQLiteAsyncConnection(databasePath);

            _customerService.SetCurrentCustomer();
            //locale resource table
            var localeResourceTable = await database.GetTableInfoAsync(nameof(LocaleResource));
            if (localeResourceTable.Count == 0)
            {
               await database.CreateTableAsync<LocaleResource>();
            }
            var anyLocaleResource = await database.Table<LocaleResource>().CountAsync();
            if (anyLocaleResource == 0)
            {
                LocaleResources = await _localizationService.GetLocaleResourcesByLanguageCultureAsync("en-US");
                foreach (var localeResource in LocaleResources)
                {
                   await database.InsertAsync(new LocaleResource()
                    {
                        LanguageId = localeResource.LanguageId,
                        ResourceName = localeResource.ResourceName,
                        ResourceValue = localeResource.ResourceValue
                    });
                }
            }
            else
            {
                var dbLocaleResources = await database.Table<LocaleResource>().ToListAsync().ConfigureAwait(true);
                LocaleResources = dbLocaleResources.Select(v => new LocaleResourceModel()
                {
                    LanguageId = v.LanguageId,
                    ResourceName = v.ResourceName,
                    ResourceValue = v.ResourceValue
                }).ToList();
            }
        }

        public static Page GetMainPage()
        {
            var page = new NavigationPage() { BindingContext = new NavigationBaseViewModel() };
            return page;
        }

        protected override void OnStart()
        {
        }

        protected override void OnSleep()
        {
        }

        protected override void OnResume()
        {
        }
    }
}
