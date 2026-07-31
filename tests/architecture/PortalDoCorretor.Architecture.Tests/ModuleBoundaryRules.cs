using System.Reflection;
using FluentAssertions;
using PortalDoCorretor.SharedKernel.Domain;

namespace PortalDoCorretor.Architecture.Tests;

/// <summary>
/// Fronteiras entre módulos. O objetivo é que a erosão da arquitetura seja uma falha de
/// build, e não uma observação em code review que alguém aceita "só desta vez".
/// </summary>
public sealed class ModuleBoundaryRules
{
    private static readonly Assembly Customers = typeof(Customers.Domain.Customer).Assembly;
    private static readonly Assembly Proposals = typeof(Proposals.Domain.Proposal).Assembly;
    private static readonly Assembly Policies = typeof(Policies.Domain.Policy).Assembly;

    private static readonly Assembly[] DomainAssemblies = [Customers, Proposals, Policies];

    private static readonly string[] ForbiddenFrameworks =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore",
        "Npgsql",
        "Dapper",
        "Serilog",
        "StackExchange.Redis",
        "FluentValidation"
    ];

    /// <summary>
    /// Regra 1: nenhum domínio depende de framework de infraestrutura. Se um atributo do
    /// EF Core precisar entrar aqui, é sinal de que o mapeamento vazou para onde não devia.
    /// </summary>
    [Fact]
    public void Dominios_nao_dependem_de_framework()
    {
        foreach (var assembly in DomainAssemblies)
        {
            var referenced = assembly.GetReferencedAssemblies()
                .Select(a => a.Name ?? string.Empty)
                .ToArray();

            foreach (var framework in ForbiddenFrameworks)
                referenced.Should().NotContain(
                    name => name.StartsWith(framework, StringComparison.Ordinal),
                    $"{assembly.GetName().Name} não pode depender de {framework}");
        }
    }

    /// <summary>
    /// Regra 3: um módulo não referencia o domínio de outro. A comunicação passa por
    /// contratos ou eventos — é o que permite extrair um módulo no futuro sem cirurgia.
    /// </summary>
    [Fact]
    public void Modulos_nao_referenciam_o_dominio_uns_dos_outros()
    {
        foreach (var assembly in DomainAssemblies)
        {
            var moduleDomains = assembly.GetReferencedAssemblies()
                .Select(a => a.Name ?? string.Empty)
                .Where(name => name.StartsWith("PortalDoCorretor.", StringComparison.Ordinal)
                            && name.EndsWith(".Domain", StringComparison.Ordinal))
                .ToArray();

            moduleDomains.Should().BeEmpty(
                $"{assembly.GetName().Name} deve comunicar-se por contratos, não por acesso direto");
        }
    }

    /// <summary>
    /// Regra 4: o grafo de referências entre módulos não tem ciclo. Verificado pela ausência
    /// de referência cruzada — com zero arestas entre domínios, não existe ciclo possível.
    /// </summary>
    [Fact]
    public void Nao_existem_ciclos_entre_modulos()
    {
        var edges = DomainAssemblies
            .SelectMany(a => a.GetReferencedAssemblies()
                .Select(r => r.Name ?? string.Empty)
                .Where(n => DomainAssemblies.Any(d => d.GetName().Name == n))
                .Select(n => (From: a.GetName().Name, To: n)))
            .ToArray();

        edges.Should().BeEmpty("módulos de domínio não se referenciam entre si");
    }

    /// <summary>
    /// Regra 7: nenhum agregado expõe coleção mutável. Uma <c>List&lt;T&gt;</c> pública deixa
    /// qualquer chamador inserir um filho sem passar pelo método de intenção do root,
    /// contornando as invariantes — é o modelo anêmico entrando pela porta dos fundos.
    /// </summary>
    [Fact]
    public void Agregados_expoem_apenas_colecoes_somente_leitura()
    {
        var offenders = AllDomainTypes()
            .Where(IsAggregateRoot)
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(p => IsMutableCollection(p.PropertyType))
            .Select(p => $"{p.DeclaringType?.Name}.{p.Name}")
            .ToArray();

        offenders.Should().BeEmpty("use IReadOnlyCollection<T> e um método de intenção no root");
    }

    /// <summary>
    /// Agregado sem factory estático significa que existe um construtor público criando
    /// estado sem passar pelas invariantes.
    /// </summary>
    [Fact]
    public void Agregados_nao_possuem_construtor_publico()
    {
        var offenders = AllDomainTypes()
            .Where(IsAggregateRoot)
            .Where(t => t.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Length > 0)
            .Select(t => t.Name)
            .ToArray();

        offenders.Should().BeEmpty(
            "a criação deve passar por factory estático, onde as invariantes são verificadas");
    }

    /// <summary>Todo agregado de negócio carrega o tenant — base do filtro global e da RLS.</summary>
    [Fact]
    public void Agregados_de_negocio_sao_escopados_por_tenant()
    {
        var offenders = AllDomainTypes()
            .Where(IsAggregateRoot)
            .Where(t => !typeof(ITenantScoped).IsAssignableFrom(t))
            .Select(t => t.Name)
            .ToArray();

        offenders.Should().BeEmpty("todo agregado de negócio deve implementar ITenantScoped");
    }

    /// <summary>
    /// Eventos de domínio são fatos consumados: imutáveis e sempre com tenant, para que a
    /// auditoria e a Outbox nunca percam a informação de isolamento.
    /// </summary>
    [Fact]
    public void Eventos_sao_imutaveis_e_carregam_tenant()
    {
        var events = AllDomainTypes()
            .Where(t => typeof(IDomainEvent).IsAssignableFrom(t))
            .Where(t => t is { IsAbstract: false, IsInterface: false })
            .ToArray();

        events.Should().NotBeEmpty("os módulos devem emitir eventos de domínio");

        foreach (var evt in events)
        {
            evt.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.SetMethod is { IsPublic: true } setter && !IsInitOnly(setter))
                .Should().BeEmpty($"{evt.Name} deve ser imutável");

            evt.GetProperty(nameof(IDomainEvent.TenantId))
                .Should().NotBeNull($"{evt.Name} deve carregar o tenant");
        }
    }

    /// <summary>
    /// Códigos de erro são constantes, não strings literais espalhadas. Isso permite que o
    /// teste verifique o contrato (o código) em vez da redação da mensagem, que pode mudar.
    /// </summary>
    [Fact]
    public void Cada_modulo_declara_seus_codigos_de_erro_como_constantes()
    {
        foreach (var assembly in DomainAssemblies)
        {
            var errorClasses = assembly.GetTypes()
                .Where(t => t.IsAbstract && t.IsSealed && t.Name.EndsWith("Errors", StringComparison.Ordinal))
                .ToArray();

            errorClasses.Should().NotBeEmpty(
                $"{assembly.GetName().Name} deve declarar uma classe de códigos de erro");

            foreach (var errorClass in errorClasses)
                errorClass.GetFields(BindingFlags.Public | BindingFlags.Static)
                    .Should().OnlyContain(f => f.IsLiteral && f.FieldType == typeof(string),
                        $"{errorClass.Name} deve conter apenas constantes string");
        }
    }

    private static IEnumerable<Type> AllDomainTypes() =>
        DomainAssemblies.SelectMany(a => a.GetTypes());

    private static bool IsAggregateRoot(Type type)
    {
        if (type is { IsAbstract: true } or { IsInterface: true }) return false;

        for (var current = type.BaseType; current is not null; current = current.BaseType)
            if (current.IsGenericType
                && current.GetGenericTypeDefinition() == typeof(AggregateRoot<>))
                return true;

        return false;
    }

    private static bool IsMutableCollection(Type type)
    {
        if (!type.IsGenericType) return false;

        var definition = type.GetGenericTypeDefinition();
        return definition == typeof(List<>)
            || definition == typeof(ICollection<>)
            || definition == typeof(IList<>)
            || definition == typeof(HashSet<>);
    }

    private static bool IsInitOnly(MethodInfo setter) =>
        setter.ReturnParameter.GetRequiredCustomModifiers()
            .Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");
}
