using GlasFlow.Application.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GlasFlow.Tests;

public class AssistantServiceTests : IDisposable
{
    private readonly TestDb _t = new();
    public void Dispose() => _t.Dispose();

    private sealed class FakeProvider(Func<string> reply) : IAiProvider
    {
        public string? LastPrompt { get; private set; }
        public string Name => "fake";
        public bool IsConfigured => true;
        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            LastPrompt = userPrompt;
            return Task.FromResult(reply());
        }
    }

    private AssistantService Create(params IAiProvider[] providers) =>
        new(new BusinessDataService(_t.Db, _t.Clock), providers, _t.Clock, NullLogger<AssistantService>.Instance);

    [Fact]
    public async Task Without_provider_returns_deterministic_answer()
    {
        var r = await Create().AskAsync("¿Qué productos tienen inventario bajo?", default);
        Assert.True(r.UsedFallback);
        Assert.Equal("local-rules", r.Provider);
        Assert.Contains("TMP-10MM", r.Answer); // border warehouse has 10 vs minimum 50
    }

    [Fact]
    public async Task Provider_receives_only_the_summary_and_its_answer_is_returned()
    {
        var fake = new FakeProvider(() => "Respuesta del modelo");
        var r = await Create(fake).AskAsync("Resume las ventas del mes.", default);
        Assert.False(r.UsedFallback);
        Assert.Equal("Respuesta del modelo", r.Answer);
        Assert.Contains("SalesSummary", fake.LastPrompt);
        Assert.DoesNotContain("PasswordHash", fake.LastPrompt);
    }

    [Fact]
    public async Task Provider_failure_falls_back_instead_of_failing()
    {
        var r = await Create(new FakeProvider(() => throw new HttpRequestException("503"))).AskAsync("¿Qué debería revisar el administrador hoy?", default);
        Assert.True(r.UsedFallback);
        Assert.Contains("Resumen del día", r.Answer);
    }
}
