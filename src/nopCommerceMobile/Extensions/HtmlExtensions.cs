﻿using nopCommerceMobile.ViewModels.Base;

namespace nopCommerceMobile.Extensions
{
    public static class HtmlExtensions
    {
        public static bool IsNotNull(this BaseViewModel model)
        {
            return model != null;
        }

        public static bool IsNull(this BaseViewModel model)
        {
            return model == null;
        }

        public static bool IsNullOrEmpty(this string text)
        {
            if (string.IsNullOrEmpty(text))
                return true;

            return false;
        }
    }
}
