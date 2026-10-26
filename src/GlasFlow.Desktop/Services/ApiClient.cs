using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlasFlow.Desktop.Services;

/// <summary>Error from the API (or from reaching it) with a message that is safe to show to the user.</summary>
public sealed class ApiException(string message, HttpStatusCode? status = null, IReadOnlyDictionary<string, string[]>? errors = null) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = status;
    public IReadOnlyDictionary<string, string[]>? Errors { get; } = errors;
}

/// <summary>The only gateway to the REST API. Holds the JWT, parses ProblemDetails and maps transport failures to friendly errors.</summary>
public sealed class ApiClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient _http;
    private string? _token;

    public string BaseUrl { get; }
    public LoginResponse? Session { get; private set; }

    /// <summary>Raised (possibly from a non-UI thread) when the API answers 401 to an authenticated call.</summary>
    public event Action? SessionExpired;

    public ApiClient(string baseUrl, HttpMessageHandler? handler = null)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(BaseUrl + "/");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<LoginResponse> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        var res = await SendAsync(HttpMethod.Post, "api/auth/login", new { username, password }, authenticated: false, ct);
        var login = await Read<LoginResponse>(res, ct);
        _token = login.Token;
        Session = login;
        return login;
    }

    public void Logout() { _token = null; Session = null; }

    // ----- Dashboard -----
    public Task<DashboardDto> GetDashboardAsync(CancellationToken ct = default) => GetAsync<DashboardDto>("api/dashboard", ct);

    // ----- Catalog -----
    public Task<IReadOnlyList<string>> GetCategoriesAsync(CancellationToken ct = default) => GetAsync<IReadOnlyList<string>>("api/products/categories", ct);
    public Task<IReadOnlyList<WarehouseDto>> GetWarehousesAsync(CancellationToken ct = default) => GetAsync<IReadOnlyList<WarehouseDto>>("api/warehouses", ct);
    public Task<IReadOnlyList<SupplierDto>> GetSuppliersAsync(CancellationToken ct = default) => GetAsync<IReadOnlyList<SupplierDto>>("api/suppliers", ct);
    public Task<PagedResult<CustomerDto>> GetCustomersAsync(int pageSize = 200, CancellationToken ct = default) =>
        GetAsync<PagedResult<CustomerDto>>($"api/customers?pageSize={pageSize}", ct);

    // ----- Inventory -----
    public Task<PagedResult<StockDto>> GetInventoryAsync(string? search, string? category, int? warehouseId, bool lowStockOnly, int page, int pageSize, CancellationToken ct = default) =>
        GetAsync<PagedResult<StockDto>>("api/inventory" + Query(("search", search), ("category", category), ("warehouseId", warehouseId?.ToString()),
            ("lowStockOnly", lowStockOnly ? "true" : null), ("page", page.ToString()), ("pageSize", pageSize.ToString())), ct);

    public Task<StockDto> AdjustStockAsync(StockAdjustmentRequest request, CancellationToken ct = default) =>
        PostAsync<StockDto>("api/inventory/adjustments", request, ct);

    // ----- Products -----
    public Task<PagedResult<ProductDto>> GetProductsAsync(string? search, int page, int pageSize, bool? active = null, CancellationToken ct = default) =>
        GetAsync<PagedResult<ProductDto>>("api/products" + Query(("search", search), ("active", active?.ToString().ToLowerInvariant()),
            ("page", page.ToString()), ("pageSize", pageSize.ToString())), ct);

    public Task<ProductDto> CreateProductAsync(ProductRequest r, CancellationToken ct = default) => PostAsync<ProductDto>("api/products", r, ct);

    public async Task<ProductDto> UpdateProductAsync(int id, ProductRequest r, CancellationToken ct = default) =>
        await Read<ProductDto>(await SendAsync(HttpMethod.Put, $"api/products/{id}", r, true, ct), ct);

    public async Task DeactivateProductAsync(int id, CancellationToken ct = default) =>
        (await SendAsync(HttpMethod.Delete, $"api/products/{id}", null, true, ct)).Dispose();

    // ----- Sales orders -----
    public Task<PagedResult<SalesOrderSummaryDto>> GetSalesOrdersAsync(string? status, string? search, int page, int pageSize, CancellationToken ct = default) =>
        GetAsync<PagedResult<SalesOrderSummaryDto>>("api/sales-orders" + Query(("status", status), ("search", search),
            ("page", page.ToString()), ("pageSize", pageSize.ToString())), ct);

    public Task<SalesOrderDto> GetSalesOrderAsync(int id, CancellationToken ct = default) => GetAsync<SalesOrderDto>($"api/sales-orders/{id}", ct);
    public Task<SalesOrderDto> CreateSalesOrderAsync(CreateSalesOrderRequest r, CancellationToken ct = default) => PostAsync<SalesOrderDto>("api/sales-orders", r, ct);
    public Task<SalesOrderDto> SalesOrderActionAsync(int id, string action, CancellationToken ct = default) =>
        PostAsync<SalesOrderDto>($"api/sales-orders/{id}/{action}", null, ct);

    // ----- plumbing -----
    public async Task<T> GetAsync<T>(string path, CancellationToken ct = default) =>
        await Read<T>(await SendAsync(HttpMethod.Get, path, null, true, ct), ct);

    public async Task<T> PostAsync<T>(string path, object? body, CancellationToken ct = default) =>
        await Read<T>(await SendAsync(HttpMethod.Post, path, body, true, ct), ct);

    private static string Query(params (string key, string? value)[] pairs)
    {
        var parts = pairs.Where(p => !string.IsNullOrWhiteSpace(p.value))
            .Select(p => $"{Uri.EscapeDataString(p.key)}={Uri.EscapeDataString(p.value!)}");
        var q = string.Join("&", parts);
        return q.Length == 0 ? "" : "?" + q;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, bool authenticated, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        if (authenticated && _token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        if (body is not null) req.Content = JsonContent.Create(body, options: Json);
        else if (method == HttpMethod.Post) req.Content = new StringContent("", Encoding.UTF8, "application/json");

        HttpResponseMessage res;
        try { res = await _http.SendAsync(req, ct); }
        catch (HttpRequestException)
        {
            throw new ApiException($"API no disponible en {BaseUrl}. Verifique que el servicio esté en ejecución.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ApiException($"La API en {BaseUrl} no respondió a tiempo.");
        }

        if (res.IsSuccessStatusCode) return res;

        try
        {
            if (res.StatusCode == HttpStatusCode.Unauthorized && authenticated)
            {
                Logout();
                SessionExpired?.Invoke();
                throw new ApiException("Su sesión expiró. Inicie sesión de nuevo.", res.StatusCode);
            }
            throw await ToException(res, ct);
        }
        finally { res.Dispose(); }
    }

    private static async Task<T> Read<T>(HttpResponseMessage res, CancellationToken ct)
    {
        using (res)
        {
            try
            {
                var value = await res.Content.ReadFromJsonAsync<T>(Json, ct);
                return value ?? throw new ApiException("La API devolvió una respuesta vacía.");
            }
            catch (JsonException ex)
            {
                throw new ApiException("Respuesta inesperada de la API: " + ex.Message);
            }
        }
    }

    /// <summary>Parses RFC 7807 ProblemDetails ({title,status,detail,errors{field:[msg]}}) into a friendly message.</summary>
    private static async Task<ApiException> ToException(HttpResponseMessage res, CancellationToken ct)
    {
        string? title = null, detail = null;
        Dictionary<string, string[]>? errors = null;
        try
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrWhiteSpace(text))
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String) title = t.GetString();
                    if (root.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String) detail = d.GetString();
                    if (root.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Object)
                    {
                        errors = [];
                        foreach (var p in e.EnumerateObject())
                            errors[p.Name] = p.Value.ValueKind == JsonValueKind.Array
                                ? p.Value.EnumerateArray().Select(x => x.GetString() ?? "").ToArray()
                                : [p.Value.ToString()];
                    }
                }
            }
        }
        catch (JsonException) { /* not JSON: use the status line */ }

        var sb = new StringBuilder();
        sb.Append(!string.IsNullOrWhiteSpace(detail) ? detail : !string.IsNullOrWhiteSpace(title) ? title : $"Error {(int)res.StatusCode} {res.ReasonPhrase}");
        if (errors is { Count: > 0 })
            foreach (var (field, msgs) in errors)
                foreach (var m in msgs) sb.Append("\n• ").Append(field).Append(": ").Append(m);
        return new ApiException(sb.ToString(), res.StatusCode, errors);
    }

    public void Dispose() => _http.Dispose();
}
