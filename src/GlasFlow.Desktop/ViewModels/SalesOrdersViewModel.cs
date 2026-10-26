using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using GlasFlow.Desktop.Infrastructure;
using GlasFlow.Desktop.Services;

namespace GlasFlow.Desktop.ViewModels;

public sealed record StatusOption(string? Value, string Label);

public sealed class SalesOrdersViewModel : PagedScreen
{
    private readonly ApiClient _api;
    private string _search = "";
    private StatusOption _status;
    private SalesOrderSummaryDto? _selected;
    private SalesOrderDto? _detail;
    private bool _suspend;

    public SalesOrdersViewModel(ApiClient api)
    {
        _api = api;
        StatusOptions =
        [
            new(null, "Todos los estados"),
            new("Draft", "Borrador"),
            new("Confirmed", "Confirmado"),
            new("Shipped", "Embarcado"),
            new("Cancelled", "Cancelado")
        ];
        _status = StatusOptions[0];
        NewCommand = new AsyncRelayCommand(NewAsync, () => NotBusy);
        ConfirmCommand = new AsyncRelayCommand(() => ActionAsync("confirm"), () => NotBusy && Detail?.Status == "Draft");
        ShipCommand = new AsyncRelayCommand(() => ActionAsync("ship"), () => NotBusy && Detail?.Status == "Confirmed");
        CancelOrderCommand = new AsyncRelayCommand(CancelAsync, () => NotBusy && Detail?.Status is "Draft" or "Confirmed");
    }

    public override string Title => "Pedidos de venta";
    public IReadOnlyList<StatusOption> StatusOptions { get; }
    public ObservableCollection<SalesOrderSummaryDto> Items { get; } = [];
    public string Search { get => _search; set => SetProperty(ref _search, value); }
    public ICommand NewCommand { get; }
    public ICommand ConfirmCommand { get; }
    public ICommand ShipCommand { get; }
    public ICommand CancelOrderCommand { get; }

    public StatusOption Status
    {
        get => _status;
        set
        {
            if (value is null || !SetProperty(ref _status, value) || _suspend) return;
            Page = 1;
            _ = LoadAsync();
        }
    }

    public SalesOrderSummaryDto? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value) || _suspend) return;
            if (value is null) Detail = null;
            else _ = LoadDetailAsync(value.Id);
        }
    }

    public SalesOrderDto? Detail { get => _detail; private set => SetProperty(ref _detail, value); }

    protected override async Task FetchAsync()
    {
        var result = await _api.GetSalesOrdersAsync(Status.Value, Search, Page, 25);
        var id = Selected?.Id;
        _suspend = true;
        try
        {
            Items.Clear();
            foreach (var o in result.Items) Items.Add(o);
            _selected = Items.FirstOrDefault(o => o.Id == id);
            OnPropertyChanged(nameof(Selected));
        }
        finally { _suspend = false; }
        ApplyPaging(result);
        if (_selected is null) Detail = null;
        else Detail = await _api.GetSalesOrderAsync(_selected.Id);
    }

    private Task LoadDetailAsync(int id) => RunAsync(async () => Detail = await _api.GetSalesOrderAsync(id));

    private async Task ActionAsync(string action)
    {
        if (Detail is null) return;
        SalesOrderDto? updated = null;
        if (await RunAsync(async () => updated = await _api.SalesOrderActionAsync(Detail.Id, action)) && updated is not null)
            await LoadAsync();
    }

    private async Task CancelAsync()
    {
        if (Detail is null) return;
        var answer = MessageBox.Show($"¿Cancelar el pedido {Detail.OrderNumber}? Se liberará el stock reservado.",
            "Cancelar pedido", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes) await ActionAsync("cancel");
    }

    private async Task NewAsync()
    {
        IReadOnlyList<CustomerDto> customers = [];
        IReadOnlyList<WarehouseDto> warehouses = [];
        IReadOnlyList<ProductDto> products = [];
        if (!await RunAsync(async () =>
            {
                customers = (await _api.GetCustomersAsync(200)).Items;
                warehouses = await _api.GetWarehousesAsync();
                products = (await _api.GetProductsAsync(null, 1, 200, active: true)).Items;
            })) return;

        var vm = new NewOrderDialogViewModel(_api, customers, warehouses, products);
        Dialogs.Show(vm);
        if (vm.Created is not null)
        {
            Page = 1;
            _suspend = true;
            _selected = null;
            _suspend = false;
            await LoadAsync();
            var created = Items.FirstOrDefault(o => o.Id == vm.Created.Id);
            if (created is not null) Selected = created;
        }
    }
}

public sealed class OrderLineEntry(ProductDto product, decimal quantity)
{
    public ProductDto Product { get; } = product;
    public decimal Quantity { get; } = quantity;
    public string Text => $"{Product.ProductCode} - {Product.ProductName}";
    public string Unit => Product.Unit;
    /// <summary>Catalog price for reference only; the server decides the real price and totals.</summary>
    public decimal ListPrice => Product.SellingPrice;
}

public sealed class NewOrderDialogViewModel : DialogViewModel
{
    private readonly ApiClient _api;
    private CustomerDto? _customer;
    private WarehouseDto? _warehouse;
    private ProductDto? _lineProduct;
    private decimal _lineQuantity = 1;
    private string _notes = "";
    private string _externalReference = "";
    private SalesOrderDto? _created;

    public NewOrderDialogViewModel(ApiClient api, IReadOnlyList<CustomerDto> customers, IReadOnlyList<WarehouseDto> warehouses, IReadOnlyList<ProductDto> products)
    {
        _api = api;
        Customers = customers;
        Warehouses = warehouses.Where(w => w.Active).ToList();
        Products = products;
        _customer = customers.FirstOrDefault();
        _warehouse = Warehouses.FirstOrDefault();
        AddLineCommand = new RelayCommand(AddLine, () => _created is null && LineProduct is not null && LineQuantity > 0);
        RemoveLineCommand = new RelayCommand(p => { if (p is OrderLineEntry l) Lines.Remove(l); });
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => NotBusy && _created is null);
        CancelCommand = new RelayCommand(() => Close(_created is not null));
    }

    public override string DialogTitle => "Nuevo pedido de venta";
    public IReadOnlyList<CustomerDto> Customers { get; }
    public IReadOnlyList<WarehouseDto> Warehouses { get; }
    public IReadOnlyList<ProductDto> Products { get; }
    public ObservableCollection<OrderLineEntry> Lines { get; } = [];

    public CustomerDto? Customer { get => _customer; set => SetProperty(ref _customer, value); }
    public WarehouseDto? Warehouse { get => _warehouse; set => SetProperty(ref _warehouse, value); }
    public ProductDto? LineProduct { get => _lineProduct; set => SetProperty(ref _lineProduct, value); }
    public decimal LineQuantity { get => _lineQuantity; set => SetProperty(ref _lineQuantity, value); }
    public string Notes { get => _notes; set => SetProperty(ref _notes, value); }
    public string ExternalReference { get => _externalReference; set => SetProperty(ref _externalReference, value); }

    /// <summary>The order as saved by the server (totals computed there). Non-null once saved.</summary>
    public SalesOrderDto? Created
    {
        get => _created;
        private set { if (SetProperty(ref _created, value)) { OnPropertyChanged(nameof(IsEditing)); OnPropertyChanged(nameof(IsSaved)); } }
    }

    public bool IsEditing => _created is null;
    public bool IsSaved => _created is not null;

    public ICommand AddLineCommand { get; }
    public ICommand RemoveLineCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    private void AddLine()
    {
        if (LineProduct is null) return;
        Lines.Add(new OrderLineEntry(LineProduct, LineQuantity));
        LineProduct = null;
        LineQuantity = 1;
    }

    private async Task SaveAsync()
    {
        if (Customer is null || Warehouse is null)
        {
            ErrorMessage = "Seleccione cliente y almacén.";
            return;
        }
        var request = new CreateSalesOrderRequest(Customer.Id, Warehouse.Id,
            string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim(),
            string.IsNullOrWhiteSpace(ExternalReference) ? null : ExternalReference.Trim(),
            Lines.Select(l => new SalesOrderLineRequest(l.Product.Id, l.Quantity)).ToList());

        SalesOrderDto? order = null;
        if (await RunAsync(async () => order = await _api.CreateSalesOrderAsync(request))) Created = order;
    }
}
