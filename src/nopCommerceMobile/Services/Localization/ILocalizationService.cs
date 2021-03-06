﻿using System.Collections.Generic;
using System.Threading.Tasks;
using nopCommerceMobile.Models.Localization;

namespace nopCommerceMobile.Services.Localization
{
    public interface ILocalizationService
    {
         Task<IList<LocaleResourceModel>> GetLocaleResourcesByIdAsync(int languageId);
         void CreateOrUpdateLocales(bool updateTable = false);
    }
}
