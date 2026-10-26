using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using GlasFlow.Desktop.Infrastructure;
using GlasFlow.Desktop.Services;

namespace GlasFlow.Desktop.ViewModels;

public sealed class ProductsViewModel : PagedScreen
{
    private readonly ApiClient _api;
    private string _search = "";
    private ProductDto? _selected;

    public ProductsViewModel(ApiClient api)
    {
        _api = api;
        NewCommand = new AsyncRelayCommand(() => EditAsync(null), () => NotBusy);
        EditCommand = new AsyncRelayCommand(() => EditAsync(Selected), () => NotBusy && Selected is not null);
        DeactivateCommand = new AsyncRelayCommand(DeactivateAsync, () => NotBusy && Selected is { Active: true });
    }

    public override string Title => "Productos";
    public ObservableCollection<ProductDto> Items { get; } = [];
    public string Search { get => _search; set => SetProperty(ref _search, value); }
    public ProductDto? Selected { get => _selected; set => SetProperty(ref _selected, value); }
    public ICommand NewCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand DeactivateCommand { get; }

    protected override async Task FetchAsync()
    {
        var result = await _api.GetProductsAsync(Search, Page, 25);
        var id = Selected?.Id;
        Items.Clear();
        foreach (var p in result.Items) Items.Add(p);
        Selected = Items.FirstOrDefault(p => p.Id == id);
        ApplyPaging(result);
    }

    private async Task EditAsync(ProductDto? product)
    {
        IReadOnlyList<string> categories = [];
        IReadOnlyList<SupplierDto> suppliers = [];
        if (!await RunAsync(async () =>
            {
                categories = await _api.GetCategoriesAsync();
                suppliers = await _api.GetSuppliersAsync();
            })) return;

        if (Dialogs.Show(new ProductDialogViewModel(_api, product, categories, suppliers))) await LoadAsync();
    }

    private async Task DeactivateAsync()
    {
        if (Selected is null) return;
        var answer = MessageBox.Show($"¿Desactivar el producto {Selected.ProductCode} - {Selected.ProductName}?\nDejará de aparecer para nuevos pedidos.",
            "Desactivar producto", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        if (await RunAsync(() => _api.DeactivateProductAsync(Selected.Id))) await LoadAsync();
    }
}

public sealed class ProductDialogViewModel : DialogViewModel
{
    private readonly ApiClient _api;
    private readonly ProductDto? _product;

    public ProductDialogViewModel(ApiClient api, ProductDto? product, IReadOnlyList<string> categories, IReadOnlyList<SupplierDto> suppliers)
    {
        _api = api;
        _product = product;
        Categories = categories;
        Suppliers = [new SupplierDto(0, "", "(Sin proveedor)"), .. suppliers];
        Units = ["m2", "pza", "ml", "kg"];

        ProductCode = product?.ProductCode ?? "";
        ExternalId = product?.ExternalId ?? "";
        ProductName = product?.ProductName ?? "";
        Category = product?.Category ?? categories.FirstOrDefault() ?? "";
        Description = product?.Description ?? "";
        Unit = product?.Unit ?? "m2";
        Cost = product?.Cost ?? 0;
        SellingPrice = product?.SellingPrice ?? 0;
        MinimumStock = product?.MinimumStock ?? 0;
        Active = product?.Active ?? true;
        SupplierId = product?.SupplierId ?? 0;

        SaveCommand = new AsyncRelayCommand(SaveAsync, () => NotBusy);
        CancelCommand = new RelayCommand(() => Close(false));
    }

    public override string DialogTitle => _product is null ? "Nuevo producto" : $"Editar producto {_product.ProductCode}";
    public IReadOnlyList<string> Categories { get; }
    public IReadOnlyList<SupplierDto> Suppliers { get; }
    public IReadOnlyList<string> Units { get; }

    public string ProductCode { get; set; }
    public string ExternalId { get; set; }
    public string ProductName { get; set; }
    public string Category { get; set; }
    public string Description { get; set; }
    public string Unit { get; set; }
    public decimal Cost { get; set; }
    public decimal SellingPrice { get; set; }
    public decimal MinimumStock { get; set; }
    public bool Active { get; set; }
    public int SupplierId { get; set; }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    private async Task SaveAsync()
    {
        // No client-side business validation: the server is the authority and its messages are shown in the banner.
        var request = new ProductRequest(ProductCode.Trim(), string.IsNullOrWhiteSpace(ExternalId) ? null : ExternalId.Trim(),
            ProductName.Trim(), Category, string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(), Unit,
            Cost, SellingPrice, MinimumStock, Active, SupplierId == 0 ? null : SupplierId);

        var ok = await RunAsync(async () =>
        {
            if (_product is null) await _api.CreateProductAsync(request);
            else await _api.UpdateProductAsync(_product.Id, request);
        });
        if (ok) Close(true);
    }
}
