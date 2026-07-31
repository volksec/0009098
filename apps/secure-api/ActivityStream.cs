using System.Threading.Channels;

namespace PortalDoCorretor.SecureApi;

/// <summary>Evento observável no Live Processing Console.</summary>
public sealed record ProcessingEvent(
    string Category,
    string Module,
    string Operation,
    string Message,
    string Status,
    string? Entity = null,
    Guid? EntityId = null,
    Guid? TenantId = null,
    Guid? CorrelationId = null,
    int? DurationMs = null,
    string? Sql = null)
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string Id { get; init; } = Guid.CreateVersion7().ToString();
}

/// <summary>
/// Barramento em memória que alimenta o stream SSE do Live Processing Console.
/// </summary>
/// <remarks>
/// <para>
/// Cada assinante recebe seu próprio canal limitado (<c>BoundedChannel</c>) com descarte do
/// item mais antigo. É deliberado: um cliente lento não pode causar acúmulo ilimitado de
/// memória no servidor nem retardar a operação de negócio que emitiu o evento — perder um
/// evento de observabilidade é preferível a derrubar a aplicação.
/// </para>
/// <para>
/// A publicação é <b>não bloqueante</b> por construção: <see cref="Publish"/> usa
/// <c>TryWrite</c> e nunca aguarda. Instrumentação não pode alterar o comportamento do
/// caminho que instrumenta.
/// </para>
/// </remarks>
public sealed class ActivityStream
{
    private const int BufferPerSubscriber = 200;
    private const int RecentHistorySize = 100;

    private readonly List<Channel<ProcessingEvent>> _subscribers = [];
    private readonly Queue<ProcessingEvent> _recent = new();
    private readonly Lock _gate = new();

    /// <summary>Publica um evento para todos os assinantes. Nunca bloqueia nem lança.</summary>
    public void Publish(ProcessingEvent processingEvent)
    {
        lock (_gate)
        {
            _recent.Enqueue(processingEvent);
            while (_recent.Count > RecentHistorySize) _recent.Dequeue();

            foreach (var subscriber in _subscribers)
                subscriber.Writer.TryWrite(processingEvent);
        }
    }

    /// <summary>Eventos recentes, para que um cliente que acabou de conectar já veja contexto.</summary>
    public IReadOnlyList<ProcessingEvent> Recent()
    {
        lock (_gate) return [.. _recent];
    }

    public ChannelReader<ProcessingEvent> Subscribe(out IDisposable subscription)
    {
        var channel = Channel.CreateBounded<ProcessingEvent>(new BoundedChannelOptions(BufferPerSubscriber)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        lock (_gate) _subscribers.Add(channel);

        subscription = new Subscription(this, channel);
        return channel.Reader;
    }

    private void Unsubscribe(Channel<ProcessingEvent> channel)
    {
        lock (_gate) _subscribers.Remove(channel);
        channel.Writer.TryComplete();
    }

    private sealed class Subscription(ActivityStream stream, Channel<ProcessingEvent> channel)
        : IDisposable
    {
        public void Dispose() => stream.Unsubscribe(channel);
    }
}

/// <summary>
/// Redação de dados sensíveis antes de qualquer emissão.
/// </summary>
/// <remarks>
/// O console é lido por humanos durante demonstrações e fica visível em tela compartilhada.
/// Documento, e-mail e telefone nunca aparecem em claro — a redação acontece aqui, e não na
/// interface, para que nenhum consumidor futuro do stream precise lembrar de mascarar.
/// </remarks>
public static class Redaction
{
    public static string MaskDocument(string? digits)
    {
        if (string.IsNullOrWhiteSpace(digits)) return "***";
        var clean = new string(digits.Where(char.IsAsciiDigit).ToArray());

        return clean.Length switch
        {
            11 => $"***.***.{clean[6..9]}-**",
            14 => $"**.***.{clean[5..8]}/****-**",
            _ => "***"
        };
    }

    public static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return "***";
        var at = email.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0) return "***";

        var local = email[..at];
        var visible = local.Length <= 2 ? local[..1] : local[..2];
        return $"{visible}{new string('*', Math.Max(3, local.Length - visible.Length))}{email[at..]}";
    }

    /// <summary>Trunca SQL longo e normaliza espaços, para caber em uma linha do console.</summary>
    public static string CompactSql(string sql, int maxLength = 220)
    {
        var single = string.Join(' ', sql.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                          .Select(line => line.Trim()));
        return single.Length <= maxLength ? single : single[..maxLength] + " …";
    }
}
