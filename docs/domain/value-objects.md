# Value Objects — PortalDoCorretor

Um Value Object (VO) é definido por **seus atributos**, não por identidade. No PortalDoCorretor todo VO
obedece a sete regras, sem exceção:

1. **Imutável** — apenas `init`/construtor privado; nenhum setter público.
2. **Autovalidado** — impossível construir em estado inválido; o construtor lança ou o factory retorna `Result<T>`.
3. **Igualdade por valor** — `record` do C#, ou `IEquatable<T>` explícito quando a comparação exige normalização.
4. **Sem *primitive obsession*** — assinaturas de domínio recebem `Money`, não `decimal`; `TenantId`, não `Guid`.
5. **Persistido corretamente** — como *owned type*, tipo composto do PostgreSQL ou coluna única com conversor.
6. **Conversões explícitas** — `Parse`/`TryParse`/`From`; nenhuma conversão implícita silenciosa que reintroduza o primitivo.
7. **Testado** — cada VO tem testes de construção válida, rejeição de inválidos, igualdade e round-trip de persistência.

## Catálogo

| VO | Encapsula | Invariante principal | Persistência |
|---|---|---|---|
| `Money` | valor + moeda | escala 2; moeda ISO-4217; soma exige mesma moeda | tipo composto `money_amount` |
| `Percentage` | fração 0–1 | 0 ≤ v ≤ 1; escala 6 | `numeric(9,6)` |
| `EmailAddress` | e-mail normalizado | formato RFC; ≤ 254; minúsculo | `citext` |
| `PhoneNumber` | telefone BR | DDD válido; 10 ou 11 dígitos | `varchar(11)` normalizado |
| `DocumentNumber` | CPF ou CNPJ | dígito verificador válido; tipo coerente | `varchar` + coluna de hash |
| `PostalAddress` | endereço completo | CEP 8 dígitos; UF válida | tipo composto `postal_address` |
| `DateRange` | vigência | início < fim | `daterange` nativo |
| `PolicyNumber` | número de apólice | formato + dígito verificador | `varchar(24)` unique |
| `ProposalNumber` | número de proposta | formato + sequência anual | `varchar(24)` unique |
| `QuotationNumber` | número de cotação | formato + sequência anual | `varchar(24)` unique |
| `CommissionRate` | percentual de comissão | 0 ≤ v ≤ 0,35 (teto de negócio) | `numeric(6,4)` |
| `RiskScore` | escore 0–1000 | inteiro no intervalo; faixa derivada | `smallint` + coluna gerada |
| `CoverageLimit` | limite de cobertura | > 0; ≤ teto do produto | tipo composto `money_amount` |
| `Deductible` | franquia | ≥ 0; tipo fixo ou percentual coerente | composto `deductible` |
| `TenantId` | identificador da corretora | UUID não vazio | `uuid` |
| `CorrelationId` | correlação de requisição | UUID v7 (ordenável no tempo) | `uuid` |

Complementares específicos do domínio: `LicensePlate`, `Vin`, `SusepRegistration`,
`ContentHash`, `IdempotencyKey`, `AccessPurpose`, `ProductCode`.

---

## Implementações de referência

### `Money` — a base de todo valor financeiro

```csharp
namespace PortalDoCorretor.SharedKernel.ValueObjects;

/// <summary>
/// Valor monetário com moeda. Escala fixa de 2 casas, arredondamento bancário.
/// Operações entre moedas distintas são proibidas por construção.
/// </summary>
public readonly record struct Money : IComparable<Money>
{
    public decimal Amount { get; }
    public Currency Currency { get; }

    private Money(decimal amount, Currency currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public static Money Of(decimal amount, Currency currency = Currency.BRL)
    {
        if (decimal.Round(amount, 2, MidpointRounding.ToEven) != amount)
            throw new DomainException(ErrorCodes.MoneyScaleInvalid,
                "Valor monetário admite no máximo 2 casas decimais.");

        if (amount is < -999_999_999.99m or > 999_999_999.99m)
            throw new DomainException(ErrorCodes.MoneyOutOfRange,
                "Valor monetário fora da faixa suportada.");

        return new Money(amount, currency);
    }

    public static Money Zero(Currency currency = Currency.BRL) => new(0m, currency);

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount + other.Amount, Currency);
    }

    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount - other.Amount, Currency);
    }

    public Money MultiplyBy(Percentage percentage) =>
        new(decimal.Round(Amount * percentage.Value, 2, MidpointRounding.ToEven), Currency);

    /// <summary>
    /// Divide em N parcelas sem perder centavos: o resíduo do arredondamento
    /// é somado à primeira parcela. Garante Σ parcelas == total.
    /// </summary>
    public IReadOnlyList<Money> Allocate(int parts)
    {
        if (parts < 1)
            throw new DomainException(ErrorCodes.AllocationInvalid,
                "Número de parcelas deve ser positivo.");

        var baseAmount = decimal.Round(Amount / parts, 2, MidpointRounding.ToZero);
        var remainder = Amount - (baseAmount * parts);

        var result = new Money[parts];
        result[0] = new Money(baseAmount + remainder, Currency);
        for (var i = 1; i < parts; i++)
            result[i] = new Money(baseAmount, Currency);

        return result;
    }

    public bool IsPositive => Amount > 0m;
    public bool IsZero => Amount == 0m;

    private void EnsureSameCurrency(Money other)
    {
        if (Currency != other.Currency)
            throw new DomainException(ErrorCodes.CurrencyMismatch,
                $"Operação entre moedas distintas: {Currency} e {other.Currency}.");
    }

    public int CompareTo(Money other)
    {
        EnsureSameCurrency(other);
        return Amount.CompareTo(other.Amount);
    }

    public override string ToString() => $"{Currency} {Amount:N2}";
}
```

**Por que `Allocate` existe:** a invariante `Σ parcelas = prêmio total` é exigida no RF-064.
Dividir `R$ 1.000,00` em 3 com arredondamento ingênuo produz `333,33 × 3 = 999,99` — um centavo
perdido que, em produção, vira divergência contábil. O método concentra a regra em um único lugar
testado, em vez de espalhá-la por cada serviço que gera parcelas.

---

### `DocumentNumber` — CPF/CNPJ sintético com validação real

```csharp
public readonly record struct DocumentNumber
{
    public string Value { get; }              // apenas dígitos
    public DocumentKind Kind { get; }         // Cpf | Cnpj

    private DocumentNumber(string value, DocumentKind kind) => (Value, Kind) = (value, kind);

    public static DocumentNumber Parse(string input)
    {
        var digits = Digits(input);

        return digits.Length switch
        {
            11 when IsValidCpf(digits)  => new DocumentNumber(digits, DocumentKind.Cpf),
            14 when IsValidCnpj(digits) => new DocumentNumber(digits, DocumentKind.Cnpj),
            _ => throw new DomainException(ErrorCodes.DocumentInvalid,
                     "Documento inválido.")   // nunca ecoa o valor recebido
        };
    }

    /// <summary>Mascaramento para exibição ao perfil regulatório e para logs.</summary>
    public string Masked => Kind == DocumentKind.Cpf
        ? $"***.***.{Value[9..]}-**"
        : $"**.***.{Value[5..8]}/****-**";

    /// <summary>
    /// Hash determinístico com pepper, usado como chave de busca e de unicidade.
    /// Permite localizar por documento sem manter o valor em claro em índice.
    /// </summary>
    public string SearchHash(ReadOnlySpan<byte> pepper) => Hashing.HmacSha256(Value, pepper);

    public override string ToString() => Masked;   // impede vazamento acidental em log
}
```

**Decisões deliberadas:**

- A exceção **nunca inclui o valor recebido** — mensagens de erro são um vetor clássico de
  vazamento de dado pessoal em log agregado.
- `ToString()` retorna a versão **mascarada**. Se um desenvolvedor interpolar o objeto em um log
  por descuido, o mascaramento é o comportamento padrão, não a exceção. Segurança por default.
- O valor em claro é cifrado em repouso; a busca usa `SearchHash` com *pepper* fora do banco,
  de modo que o vazamento do dump não permite ataque de dicionário sobre CPFs (o espaço de CPFs
  é pequeno o bastante para força bruta sem o pepper).

---

### `DateRange` — vigência mapeada a tipo nativo do PostgreSQL

```csharp
public readonly record struct DateRange
{
    public DateOnly Start { get; }
    public DateOnly End { get; }

    private DateRange(DateOnly start, DateOnly end) => (Start, End) = (start, end);

    public static DateRange Of(DateOnly start, DateOnly end)
    {
        if (end <= start)
            throw new DomainException(ErrorCodes.DateRangeInvalid,
                "A data final deve ser posterior à inicial.");
        return new DateRange(start, end);
    }

    public static DateRange OfMonths(DateOnly start, int months) =>
        Of(start, start.AddMonths(months));

    public bool Contains(DateOnly date) => date >= Start && date < End;
    public bool Overlaps(DateRange other) => Start < other.End && other.Start < End;
    public int DurationInDays => End.DayNumber - Start.DayNumber;
    public bool IsExpiringWithin(DateOnly reference, int days) =>
        End > reference && End <= reference.AddDays(days);
}
```

Mapeado para `daterange` nativo, o que permite que a **constraint de exclusão** do PostgreSQL
impeça sobreposição de vigência para o mesmo bem — a mesma regra `Overlaps` do domínio, agora
também garantida pelo banco (defesa em profundidade, RNF-024):

```sql
ALTER TABLE policies ADD CONSTRAINT ex_policies_no_overlap
  EXCLUDE USING gist (
      tenant_id      WITH =,
      asset_id       WITH =,
      product_id     WITH =,
      coverage_period WITH &&
  ) WHERE (status = 'ACTIVE' AND deleted_at IS NULL);
```

---

### `PolicyNumber` — identificador de negócio com verificação

```csharp
public readonly record struct PolicyNumber
{
    private static readonly Regex Pattern =
        new(@"^PC-(?<year>\d{4})-(?<seq>\d{8})-(?<dv>\d)$", RegexOptions.Compiled);

    public string Value { get; }
    private PolicyNumber(string value) => Value = value;

    public static PolicyNumber Parse(string input)
    {
        var match = Pattern.Match(input?.Trim().ToUpperInvariant() ?? string.Empty);
        if (!match.Success)
            throw new DomainException(ErrorCodes.PolicyNumberInvalid,
                "Número de apólice em formato inválido.");

        var payload = $"{match.Groups["year"].Value}{match.Groups["seq"].Value}";
        if (CheckDigit.Mod11(payload) != int.Parse(match.Groups["dv"].Value))
            throw new DomainException(ErrorCodes.PolicyNumberCheckDigit,
                "Dígito verificador do número de apólice inválido.");

        return new PolicyNumber(match.Value);
    }

    /// <summary>Gerado a partir de sequence do banco — garante unicidade sob concorrência.</summary>
    public static PolicyNumber Generate(int year, long sequence)
    {
        var payload = $"{year:D4}{sequence:D8}";
        return new PolicyNumber($"PC-{year:D4}-{sequence:D8}-{CheckDigit.Mod11(payload)}");
    }
}
```

O dígito verificador não é enfeite: torna inválido um número de apólice adivinhado por
incremento, o que transforma tentativas de enumeração em erro de validação **antes** de tocar o
banco — e o erro é registrado como `SecurityEvent` de enumeração.

---

### `RiskScore` — escore com faixa derivada

```csharp
public readonly record struct RiskScore
{
    public int Value { get; }                 // 0..1000
    private RiskScore(int value) => Value = value;

    public static RiskScore Of(int value) => value is >= 0 and <= 1000
        ? new RiskScore(value)
        : throw new DomainException(ErrorCodes.RiskScoreOutOfRange,
              "Escore de risco deve estar entre 0 e 1000.");

    public RiskBand Band => Value switch
    {
        <= 250 => RiskBand.Low,
        <= 550 => RiskBand.Moderate,
        <= 800 => RiskBand.High,
        _      => RiskBand.Severe
    };

    public bool IsAcceptableFor(ProductVersion product) => Value <= product.MaxAcceptableRiskScore;
}
```

A faixa é **derivada**, não armazenada como campo editável — não existe estado em que escore e
faixa possam divergir. No banco, a mesma derivação é replicada como coluna gerada e indexada,
para permitir filtro eficiente por faixa sem recalcular na aplicação.

---

### `TenantId` — o VO mais importante para a segurança

```csharp
public readonly record struct TenantId
{
    public Guid Value { get; }
    private TenantId(Guid value) => Value = value;

    /// <summary>
    /// Construção permitida SOMENTE a partir de claim autenticado ou de leitura do banco.
    /// Não existe overload público que aceite string vinda de requisição.
    /// </summary>
    internal static TenantId FromTrustedSource(Guid value) => value == Guid.Empty
        ? throw new DomainException(ErrorCodes.TenantIdInvalid, "TenantId não pode ser vazio.")
        : new TenantId(value);

    public override string ToString() => Value.ToString();
}
```

A ausência de um construtor público que aceite entrada do usuário é **intencional e é o ponto
central do isolamento multi-tenant**: torna a manipulação de `tenantId` via payload
impossível *por tipagem*, não apenas por validação em runtime. Um DTO de requisição simplesmente
não consegue produzir um `TenantId` válido. Isso é a primeira das cinco camadas do RNF-001.

---

## Estratégia de persistência

| Técnica | Quando | Exemplo |
|---|---|---|
| **Owned type do EF Core** | VO multi-campo pertencente a um agregado, sem reuso independente | `PostalAddress` dentro de `Address` |
| **Tipo composto do PostgreSQL** | VO reutilizado em muitas tabelas, onde a coesão física ajuda a legibilidade e evita colunas repetidas | `money_amount`, `deductible` |
| **Conversor de valor (coluna única)** | VO de campo único | `PolicyNumber`, `TenantId`, `RiskScore` |
| **Tipo nativo** | Quando o PostgreSQL já modela o conceito melhor que qualquer coluna avulsa | `DateRange` → `daterange`; `EmailAddress` → `citext` |

Exemplo de tipo composto para `Money`:

```sql
CREATE TYPE money_amount AS (
    amount    numeric(14,2),
    currency  char(3)
);

-- Uso, com constraint garantindo a invariante do VO também no banco:
ALTER TABLE policies
  ADD COLUMN total_premium money_amount NOT NULL,
  ADD CONSTRAINT ck_policies_premium_positive
      CHECK ((total_premium).amount > 0),
  ADD CONSTRAINT ck_policies_premium_currency
      CHECK ((total_premium).currency = 'BRL');
```

**Por que replicar a invariante no banco?** O VO garante que a *aplicação* não crie estado
inválido. A constraint garante que **nada** crie estado inválido — nem migration mal escrita, nem
script de correção manual, nem a aplicação vulnerável do laboratório. É exatamente essa diferença
que o Security Lab demonstra: o banco vulnerável não tem essas constraints, e o resultado aparece
em segundos.

## Testes

Cada VO recebe quatro classes de teste (`tests/unit/ValueObjects`):

1. **Construção válida** — entradas de fronteira aceitas.
2. **Rejeição** — cada invariante violada isoladamente, verificando o **código de erro** e que a
   mensagem **não contém o valor de entrada**.
3. **Igualdade** — valor igual ⇒ objetos iguais e mesmo hash; ordenação quando aplicável.
4. **Round-trip de persistência** — grava e relê via Testcontainers com PostgreSQL real,
   confirmando que o VO reconstruído é igual ao original (inclusive escala e moeda).

`Money.Allocate` recebe teste baseado em propriedade: para qualquer valor e qualquer número de
parcelas, `Σ parcelas == total` e nenhuma parcela difere de outra em mais de um centavo.
