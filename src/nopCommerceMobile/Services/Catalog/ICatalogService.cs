﻿using System.Collections.ObjectModel;
using System.Threading.Tasks;
using nopCommerceMobile.Models.Catalog;
using nopCommerceMobile.Models.News;

namespace nopCommerceMobile.Services.Catalog
{
    public interface ICatalogService
    {
        Task<ObservableCollection<CategoryModel>> GetHomeCategoriesAsync();
        Task<ObservableCollection<ProductModel>> GetHomeProductsAsync();
        Task<ObservableCollection<ProductModel>> GetHomeBestSellersAsync();
        Task<ObservableCollection<NewsItemModel>> GetHomeNewsAsync();
        Task<CategoryModel> GetCategoryByIdAsync(int categoryId);
    }
}
