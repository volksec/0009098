using System.Reflection;
using FluentAssertions;
using NetArchTest.Rules;
using PortalDoCorretor.SharedKernel.Domain;

namespace PortalDoCorretor.Architecture.Tests;

/// <summary>
/// Regras arquiteturais aplicadas ao SharedKernel. O objetivo é transformar decisões de
/// modelagem e de segurança em <b>falha de build</b>, em vez de convenções que erodem.
/// Conforme novos módulos entram (Fase 4), as mesmas regras se estendem a eles.
/// </summary>
public sealed class SharedKernelRules
{
    private static readonly Assembly SharedKernel = typeof(TenantIdMarker).Assembly;

    // Tipo âncora para localizar o assembly sem depender de um tipo específico
    private sealed class TenantIdMarker;

    private static Types SharedKernelTypes => Types.InAssembly(typeof(AggregateRoot<>).Assembly);

    /// <summary>
    /// Regra 1 (ADR-0002): o domínio não conhece framework algum. Se um dia alguém precisar
    /// de um atributo do EF Core dentro do domínio, é sinal de que o mapeamento vazou para
    /// onde não devia — e a dependência deve ser invertida, não acomodada.
    /// </summary>
    [Fact]
    public void Dominio_nao_depende_de_framework_de_infraestrutura()
    {
        string[] forbidden =
        [
            "Microsoft.EntityFrameworkCore",
            "Microsoft.AspNetCore",
            "Npgsql",
            "Dapper",
            "Serilog",
            "StackExchange.Redis"
        ];

        var referenced = typeof(AggregateRoot<>).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        foreach (var framework in forbidden)
            referenced.Should().NotContain(name => name.StartsWith(framework, StringComparison.Ordinal),
                $"o domínio não pode depender de {framework}");
    }

    /// <summary>
    /// Regra 7: agregado não expõe coleção mutável. Uma <c>List&lt;T&gt;</c> pública permite
    /// que qualquer chamador insira um filho sem passar pelo método de intenção do root,
    /// contornando as invariantes — é o modelo anêmico entrando pela porta dos fundos.
    /// </summary>
    [Fact]
    public void Agregados_nao_expoem_colecao_mutavel()
    {
        var offenders = SharedKernelTypes
            .That().Inherit(typeof(AggregateRoot<>))
            .GetTypes()
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(p => IsMutableCollection(p.PropertyType))
            .Select(p => $"{p.DeclaringType?.Name}.{p.Name}")
            .ToArray();

        offenders.Should().BeEmpty(
            "coleções de agregado devem ser expostas como IReadOnlyCollection<T>");
    }

    /// <summary>
    /// Value Objects são imutáveis: nenhuma propriedade pública com setter acessível.
    /// </summary>
    [Fact]
    public void Value_objects_nao_expoem_setter_publico()
    {
        var offenders = SharedKernelTypes
            .That().ResideInNamespace("PortalDoCorretor.SharedKernel.ValueObjects")
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } || t.IsValueType)
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(p => p.SetMethod is { IsPublic: true })
            .Select(p => $"{p.DeclaringType?.Name}.{p.Name}")
            .ToArray();

        offenders.Should().BeEmpty("Value Objects devem ser imutáveis");
    }

    /// <summary>
    /// Camada 1 da defesa em profundidade (ADR-0004): não pode existir caminho público que
    /// construa um <c>TenantId</c> a partir de entrada do usuário. Se este teste falhar,
    /// o isolamento multi-tenant deixou de ser garantido pelo sistema de tipos.
    /// </summary>
    [Fact]
    public void TenantId_so_pode_ser_criado_a_partir_de_origem_confiavel()
    {
        var tenantId = typeof(SharedKernel.ValueObjects.TenantId);

        tenantId.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Should().BeEmpty("TenantId não deve ter construtor público");

        tenantId.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == tenantId)
            .Select(m => m.Name)
            .Should().BeEquivalentTo(["FromTrustedSource"],
                "a única origem permitida é o claim autenticado ou a leitura do banco");
    }

    /// <summary>
    /// Value Objects que carregam dado pessoal devem sobrescrever <c>ToString</c> para
    /// retornar a forma mascarada — segurança por padrão contra interpolação acidental em log.
    /// </summary>
    [Fact]
    public void Value_objects_com_dado_pessoal_mascaram_no_ToString()
    {
        Type[] sensitiveTypes =
        [
            typeof(SharedKernel.ValueObjects.DocumentNumber),
            typeof(SharedKernel.ValueObjects.EmailAddress),
            typeof(SharedKernel.ValueObjects.PhoneNumber),
            typeof(SharedKernel.ValueObjects.PostalAddress)
        ];

        foreach (var type in sensitiveTypes)
        {
            var toString = type.GetMethod(nameof(ToString), Type.EmptyTypes);

            toString!.DeclaringType.Should().Be(type,
                $"{type.Name} deve sobrescrever ToString para mascarar o dado sensível");
        }
    }

    /// <summary>
    /// Eventos de domínio são fatos consumados: imutáveis e sempre com contexto de tenant,
    /// para que a auditoria e a Outbox nunca percam a informação de isolamento.
    /// </summary>
    [Fact]
    public void Eventos_de_dominio_sao_imutaveis_e_tem_tenant()
    {
        var events = SharedKernelTypes
            .That().ImplementInterface(typeof(IDomainEvent))
            .GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false })
            .ToArray();

        foreach (var evt in events)
        {
            evt.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.SetMethod is { IsPublic: true } setter && !IsInitOnly(setter))
                .Should().BeEmpty($"{evt.Name} deve ser imutável");

            evt.GetProperty(nameof(IDomainEvent.TenantId))
                .Should().NotBeNull($"{evt.Name} deve carregar o tenant");
        }
    }

    private static bool IsMutableCollection(Type type) =>
        type.IsGenericType
        && type.GetGenericTypeDefinition() is var definition
        && (definition == typeof(List<>)
            || definition == typeof(ICollection<>)
            || definition == typeof(IList<>)
            || definition == typeof(HashSet<>));

    private static bool IsInitOnly(MethodInfo setter) =>
        setter.ReturnParameter.GetRequiredCustomModifiers()
            .Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");
}
