using FluentAssertions;
using PortalDoCorretor.Persistence;
using PortalDoCorretor.SharedKernel.Errors;
using PortalDoCorretor.SharedKernel.ValueObjects;

namespace PortalDoCorretor.Domain.Tests;

/// <summary>
/// Camada 2 da defesa em profundidade: o contexto de tenant é imutável por requisição.
/// </summary>
public sealed class TenantContextTests
{
    private static TenantContext Resolved(UserProfile profile = UserProfile.Broker, Guid? tenant = null)
    {
        var context = new TenantContext();
        context.Resolve(tenant ?? Guid.NewGuid(), Guid.NewGuid(), profile, CorrelationId.New());
        return context;
    }

    [Fact]
    public void Contexto_nao_resolvido_falha_fechado()
    {
        var context = new TenantContext();

        // Falha fechado: acessar o tenant sem resolução lança, em vez de devolver
        // Guid.Empty — que passaria silenciosamente pelo filtro e retornaria zero linhas
        FluentActions.Invoking(() => context.Current)
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be("TENANT_NOT_RESOLVED");

        FluentActions.Invoking(() => context.ActorId).Should().Throw<DomainException>();
        FluentActions.Invoking(() => context.Profile).Should().Throw<DomainException>();
    }

    /// <summary>
    /// A invariante central: uma vez resolvido, o contexto não muda. Sem isso, qualquer
    /// código depois da autenticação poderia trocar de tenant no meio da requisição.
    /// </summary>
    [Fact]
    public void Contexto_resolvido_nao_pode_ser_alterado()
    {
        var context = Resolved();

        FluentActions.Invoking(() =>
                context.Resolve(Guid.NewGuid(), Guid.NewGuid(), UserProfile.Broker, CorrelationId.New()))
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be("TENANT_ALREADY_RESOLVED");
    }

    [Fact]
    public void Corretor_tem_tenant_fixo()
    {
        var tenantId = Guid.NewGuid();
        var context = Resolved(UserProfile.Broker, tenantId);

        context.Current.Value.Should().Be(tenantId);
        context.IsResolved.Should().BeTrue();
    }

    /// <summary>
    /// O perfil de supervisão é multi-tenant por escopo, então não tem tenant fixo — e
    /// acessar <c>Current</c> nesse perfil deve lançar, não devolver um valor arbitrário.
    /// </summary>
    [Fact]
    public void Perfil_regulatorio_nao_possui_tenant_fixo()
    {
        var context = Resolved(UserProfile.Regulator);

        context.IsResolved.Should().BeTrue();
        context.Profile.Should().Be(UserProfile.Regulator);

        FluentActions.Invoking(() => context.Current)
            .Should().Throw<DomainException>()
            .Which.Code.Should().Be("TENANT_NOT_RESOLVED");
    }

    [Fact]
    public void CorrelationId_e_preservado_da_requisicao()
    {
        var correlationId = CorrelationId.New();
        var context = new TenantContext();

        context.Resolve(Guid.NewGuid(), Guid.NewGuid(), UserProfile.Broker, correlationId);

        context.CorrelationId.Should().Be(correlationId);
    }

    /// <summary>
    /// <c>Resolve</c> é o único método público que altera estado. Se aparecer outro setter,
    /// a imutabilidade do contexto deixou de ser garantida.
    /// </summary>
    [Fact]
    public void Apenas_Resolve_altera_o_contexto()
    {
        var mutators = typeof(TenantContext)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(m => !m.IsSpecialName && m.DeclaringType == typeof(TenantContext))
            .Select(m => m.Name)
            .ToArray();

        mutators.Should().BeEquivalentTo([nameof(TenantContext.Resolve)]);

        typeof(ITenantContext).GetProperties()
            .Where(p => p.SetMethod is not null)
            .Should().BeEmpty("a interface consumida pelo domínio é somente leitura");
    }
}
