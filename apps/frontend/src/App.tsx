import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  api, onRequest, probeCrossTenant,
  type Brokerage, type DashboardSummary,
  type Invariant, type LastRequest, type Policy, type RlsPolicy, type SchemaStats,
} from './api'
import { CustomerAdmin } from './CustomerAdmin'
import { LiveConsole } from './LiveConsole'

type Page = 'dashboard' | 'admin' | 'policies' | 'console' | 'engineering' | 'isolation'

const PAGES: { id: Page; label: string; group?: string }[] = [
  { id: 'dashboard', label: 'Painel', group: 'Operação' },
  { id: 'admin', label: 'Administração', group: 'Operação' },
  { id: 'policies', label: 'Apólices', group: 'Operação' },
  { id: 'console', label: 'Live Console', group: 'Engenharia' },
  { id: 'engineering', label: 'Banco de dados', group: 'Engenharia' },
  { id: 'isolation', label: 'Isolamento', group: 'Engenharia' },
]

const money = (value: number) =>
  new Intl.NumberFormat('pt-BR', { style: 'currency', currency: 'BRL' }).format(value)

const shortDate = (value: string) =>
  new Date(value).toLocaleDateString('pt-BR', { day: '2-digit', month: '2-digit', year: 'numeric' })

/** Hook simples de carregamento — evita trazer TanStack Query para uma fatia deste tamanho. */
function useAsync<T>(loader: () => Promise<T>, deps: unknown[]) {
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    let cancelled = false
    setLoading(true)
    setError(null)

    loader()
      .then((result) => { if (!cancelled) setData(result) })
      .catch((err: Error) => { if (!cancelled) setError(err.message) })
      .finally(() => { if (!cancelled) setLoading(false) })

    return () => { cancelled = true }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps)

  return { data, error, loading }
}

function Panel({ title, subtitle, action, children }: {
  title: string; subtitle?: string; action?: React.ReactNode; children: React.ReactNode
}) {
  return (
    <section className="panel">
      <header className="panel-head">
        <div>
          <h2>{title}</h2>
          {subtitle && <div className="sub">{subtitle}</div>}
        </div>
        {action}
      </header>
      {children}
    </section>
  )
}

function StatusBadge({ status }: { status: string }) {
  const tone =
    status === 'ACTIVE' ? 'ok' :
    status === 'CANCELLED' || status === 'REJECTED' ? 'danger' :
    status === 'PENDING' || status === 'UNDER_ANALYSIS' ? 'warn' : 'info'
  return <span className={`badge ${tone}`}>{status}</span>
}

// ---------------------------------------------------------------- páginas

function DashboardPage({ tenantId }: { tenantId: string }) {
  const { data, loading, error } = useAsync<DashboardSummary>(
    () => api.dashboard(tenantId), [tenantId])

  if (loading) return <div className="state">Carregando…</div>
  if (error) return <div className="state">Falha: {error}</div>
  if (!data) return null

  const cards = [
    { label: 'Clientes', value: data.customers, hint: 'na carteira do tenant' },
    { label: 'Cotações abertas', value: data.openQuotations, hint: 'status CALCULATED' },
    { label: 'Propostas pendentes', value: data.pendingProposals, hint: 'aguardando análise' },
    { label: 'Apólices ativas', value: data.activePolicies, hint: 'vigência corrente' },
    { label: 'Sinistros abertos', value: data.openClaims, hint: 'em acompanhamento' },
    { label: 'Renovações', value: data.upcomingRenewals, hint: 'vencendo em 45 dias' },
  ]

  return (
    <>
      <div className="grid">
        {cards.map((card) => (
          <div className="card" key={card.label}>
            <div className="label">{card.label}</div>
            <div className="value">{card.value}</div>
            <div className="hint">{card.hint}</div>
          </div>
        ))}
      </div>

      <div className="card" style={{ marginTop: 13 }}>
        <div className="label">Comissão prevista</div>
        <div className="value">{money(data.forecastCommission)}</div>
        <div className="hint">soma de FORECAST e RELEASED — valor simulado</div>
      </div>

      {data.forecastCommission === 0 && (
        <div className="note warn">
          <strong>A comissão aparece zerada — e isso está correto.</strong> A tabela{' '}
          <code>commissions</code> tem uma política <code>RESTRICTIVE</code> adicional que filtra
          por <code>broker_id = app.current_actor()</code>: um corretor enxerga apenas as próprias
          comissões, nunca as do colega, mesmo dentro do próprio tenant. Como esta fatia ainda não
          tem autenticação, o ator da requisição é vazio e nenhuma linha satisfaz a política.
          <br /><br />
          É a segunda dimensão de autorização (ABAC) atuando sobre a primeira (tenant). O valor
          passa a aparecer quando o login estiver implementado e o ator for um corretor real.
        </div>
      )}

      <div className="note">
        Todos os números vêm de agregações executadas no PostgreSQL a cada requisição, sob o
        contexto de tenant aplicado via <code>set_config('app.tenant_id', …)</code>. Trocar a
        corretora no seletor lateral muda o conjunto de linhas que a Row-Level Security torna
        visível — a consulta é a mesma.
      </div>
    </>
  )
}

function PoliciesPage({ tenantId }: { tenantId: string }) {
  const { data, loading, error } = useAsync<Policy[]>(() => api.policies(tenantId), [tenantId])

  return (
    <Panel title="Apólices" subtitle="Vigência armazenada como daterange nativo do PostgreSQL">
      {loading && <div className="state">Carregando…</div>}
      {error && <div className="state">Falha: {error}</div>}
      {data && data.length === 0 && <div className="state">Nenhuma apólice neste tenant.</div>}

      {data && data.length > 0 && (
        <table>
          <thead>
            <tr>
              <th>Número</th><th>Cliente</th><th>Produto</th>
              <th>Vigência</th><th className="num">Prêmio</th><th>Status</th>
            </tr>
          </thead>
          <tbody>
            {data.map((policy) => (
              <tr key={policy.id}>
                <td className="mono">{policy.number}</td>
                <td>{policy.customerName}</td>
                <td>{policy.productName}</td>
                <td>{shortDate(policy.periodStart)} → {shortDate(policy.periodEnd)}</td>
                <td className="num">{money(policy.totalPremium)}</td>
                <td><StatusBadge status={policy.status} /></td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </Panel>
  )
}

function EngineeringPage() {
  const schema = useAsync<SchemaStats>(() => api.schema(), [])
  const rls = useAsync<RlsPolicy[]>(() => api.rls(), [])
  const invariants = useAsync<Invariant[]>(() => api.invariants(), [])

  const exclusions = useMemo(
    () => invariants.data?.filter((i) => i.kind === 'EXCLUSION') ?? [],
    [invariants.data])

  return (
    <>
      {schema.data && (
        <div className="grid">
          {[
            ['Tabelas', schema.data.tables],
            ['Índices', schema.data.indexes],
            ['Tabelas com RLS', schema.data.tablesWithRls],
            ['Políticas RLS', schema.data.rlsPolicies],
            ['Partições', schema.data.partitions],
            ['Constraints de exclusão', schema.data.exclusionConstraints],
            ['Enums', schema.data.enums],
            ['Tipos compostos', schema.data.compositeTypes],
          ].map(([label, value]) => (
            <div className="card" key={label as string}>
              <div className="label">{label}</div>
              <div className="value">{value}</div>
            </div>
          ))}
        </div>
      )}

      <div className="note">
        Estes números são lidos do <strong>catálogo do PostgreSQL</strong> a cada requisição
        (<code>pg_tables</code>, <code>pg_policies</code>, <code>pg_constraint</code>) — não de uma
        lista mantida no código. Se uma migration criar uma tabela nova, ela aparece aqui sem que
        nada precise ser atualizado.
      </div>

      <Panel
        title="Constraints de exclusão"
        subtitle="Invariantes que UNIQUE não consegue expressar"
      >
        {exclusions.length === 0 && <div className="state">Carregando…</div>}
        {exclusions.length > 0 && (
          <table>
            <thead><tr><th>Tabela</th><th>Constraint</th><th>Definição</th></tr></thead>
            <tbody>
              {exclusions.map((item) => (
                <tr key={item.name}>
                  <td className="mono">{item.table}</td>
                  <td className="mono">{item.name}</td>
                  <td className="mono" style={{ fontSize: 11.5 }}>{item.definition}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Panel>

      <Panel
        title="Políticas de Row-Level Security"
        subtitle={`${rls.data?.length ?? 0} políticas ativas — a coluna FORCE indica se o próprio dono da tabela também é filtrado`}
      >
        {rls.loading && <div className="state">Carregando…</div>}
        {rls.data && (
          <table>
            <thead>
              <tr><th>Tabela</th><th>Política</th><th>Comando</th><th>Papéis</th><th>FORCE</th></tr>
            </thead>
            <tbody>
              {rls.data.slice(0, 40).map((policy) => (
                <tr key={`${policy.table}-${policy.policy}`}>
                  <td className="mono">{policy.table}</td>
                  <td className="mono" style={{ fontSize: 12 }}>{policy.policy}</td>
                  <td><span className="badge muted">{policy.command}</span></td>
                  <td style={{ fontSize: 12 }}>{policy.roles}</td>
                  <td>
                    <span className={`badge ${policy.forced ? 'ok' : 'danger'}`}>
                      {policy.forced ? 'SIM' : 'NÃO'}
                    </span>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Panel>
    </>
  )
}

function IsolationPage({ tenantId, brokerages }: { tenantId: string; brokerages: Brokerage[] }) {
  const others = brokerages.filter((b) => b.id !== tenantId)
  const [targetTenant, setTargetTenant] = useState(others[0]?.id ?? '')
  const [result, setResult] = useState<{ status: number; durationMs: number; id: string } | null>(null)
  const [running, setRunning] = useState(false)

  const run = useCallback(async () => {
    if (!targetTenant) return
    setRunning(true)
    setResult(null)
    try {
      // Pega um cliente REAL do outro tenant e tenta acessá-lo com o tenant corrente
      const foreign = await api.customers(targetTenant, { pageSize: 1 })
      if (foreign.items.length === 0) { setRunning(false); return }

      const victim = foreign.items[0]
      const probe = await probeCrossTenant(tenantId, victim.id)
      setResult({ status: probe.status, durationMs: probe.durationMs, id: victim.id })
    } finally {
      setRunning(false)
    }
  }, [targetTenant, tenantId])

  return (
    <Panel
      title="Isolamento entre corretoras"
      subtitle="Tenta acessar um cliente de outro tenant usando o identificador exato"
    >
      <div style={{ padding: 16 }}>
        <p style={{ marginTop: 0, fontSize: 13.5, color: 'var(--pdc-slate-700)' }}>
          O teste busca um cliente real de outra corretora, captura o <code>id</code> dele e
          solicita <code>GET /api/customers/{'{id}'}</code> com o tenant corrente. É o cenário
          IDOR: o atacante já conhece o identificador.
        </p>

        <div style={{ display: 'flex', gap: 10, alignItems: 'center', flexWrap: 'wrap' }}>
          <select
            className="search"
            value={targetTenant}
            onChange={(event) => setTargetTenant(event.target.value)}
          >
            {others.map((b) => (
              <option key={b.id} value={b.id}>Cliente da {b.tradeName}</option>
            ))}
          </select>
          <button className="btn" onClick={run} disabled={running}>
            {running ? 'Executando…' : 'Executar tentativa'}
          </button>
        </div>

        {result && (
          <div className={`result-box ${result.status === 404 ? 'blocked' : 'allowed'}`}>
            {result.status === 404 ? (
              <>
                <strong>Bloqueado — HTTP 404</strong> em {result.durationMs} ms.<br />
                O recurso <code className="mono">{result.id}</code> existe no banco, mas a
                Row-Level Security o torna invisível para este tenant, então a consulta retorna
                zero linhas e a API responde 404.<br />
                <br />
                A resposta é <strong>404 e não 403</strong> de propósito: um 403 confirmaria que o
                recurso existe, transformando o controle de acesso em oráculo de enumeração.
              </>
            ) : (
              <>
                <strong>Retornou HTTP {result.status}</strong> — o isolamento falhou.
              </>
            )}
          </div>
        )}

        <div className="note">
          Este é o comportamento da camada 5 (RLS). As outras quatro — claim do token, contexto
          imutável, filtro global do ORM e autorização por recurso — atuam antes e são verificadas
          na suíte de testes de integração.
        </div>
      </div>
    </Panel>
  )
}

// ---------------------------------------------------------------- shell

export default function App() {
  const [page, setPage] = useState<Page>('dashboard')
  const [tenantId, setTenantId] = useState<string>('')
  const [lastRequest, setLastRequest] = useState<LastRequest | null>(null)

  const { data: brokerages } = useAsync<Brokerage[]>(() => api.brokerages(), [])

  useEffect(() => onRequest(setLastRequest), [])
  useEffect(() => {
    if (!tenantId && brokerages && brokerages.length > 0) setTenantId(brokerages[0].id)
  }, [brokerages, tenantId])

  const current = brokerages?.find((b) => b.id === tenantId)

  const heading: Record<Page, { title: string; subtitle: string }> = {
    dashboard: { title: 'Painel do corretor', subtitle: 'Indicadores da carteira no tenant selecionado' },
    admin: {
      title: 'Administração de clientes',
      subtitle: 'Cadastro, edição e exclusão lógica persistidos diretamente no PostgreSQL',
    },
    policies: { title: 'Apólices', subtitle: 'Contratos emitidos e suas vigências' },
    console: {
      title: 'Live Processing Console',
      subtitle: 'Eventos internos da aplicação em tempo real, via Server-Sent Events',
    },
    engineering: { title: 'Banco de dados', subtitle: 'Estrutura lida do catálogo do PostgreSQL em tempo real' },
    isolation: { title: 'Isolamento', subtitle: 'Demonstração executável do controle multi-tenant' },
  }

  return (
    <div className="app">
      <aside className="sidebar">
        <div className="brand">
          <div className="brand-mark">PC</div>
          <div>
            <div className="brand-name">Portal do Corretor</div>
            <div className="brand-sub">Gestão de seguros</div>
          </div>
        </div>

        <nav className="nav">
          {PAGES.map((item) => (
            <button
              key={item.id}
              aria-current={page === item.id}
              onClick={() => setPage(item.id)}
            >
              {item.label}
            </button>
          ))}
        </nav>

        <div className="tenant-picker">
          <label htmlFor="tenant">Corretora (tenant)</label>
          <select
            id="tenant"
            value={tenantId}
            onChange={(event) => setTenantId(event.target.value)}
          >
            {brokerages?.map((b) => (
              <option key={b.id} value={b.id}>{b.tradeName}</option>
            ))}
          </select>
        </div>
      </aside>

      <main className="content">
        <div className="page-head">
          <h1>{heading[page].title}</h1>
          <p>
            {heading[page].subtitle}
            {current && page !== 'engineering' && <> · <strong>{current.tradeName}</strong></>}
          </p>
        </div>

        {!tenantId && <div className="state">Carregando corretoras…</div>}

        {tenantId && page === 'dashboard' && <DashboardPage tenantId={tenantId} />}
        {tenantId && page === 'admin' && <CustomerAdmin key={tenantId} tenantId={tenantId} />}
        {page === 'console' && <LiveConsole />}
        {tenantId && page === 'policies' && <PoliciesPage tenantId={tenantId} />}
        {page === 'engineering' && <EngineeringPage />}
        {tenantId && page === 'isolation' && (
          <IsolationPage tenantId={tenantId} brokerages={brokerages ?? []} />
        )}
      </main>

      <footer className="trace-bar">
        <span><span className="k">tenant</span> <span className="v mono">{tenantId.slice(0, 8) || '—'}</span></span>
        <span><span className="k">rota</span> <span className="v mono">{lastRequest?.path ?? '—'}</span></span>
        <span><span className="k">status</span> <span className="v mono">{lastRequest?.status ?? '—'}</span></span>
        <span><span className="k">duração</span> <span className="v mono">{lastRequest ? `${lastRequest.durationMs} ms` : '—'}</span></span>
        <span><span className="k">correlation-id</span> <span className="v mono">{lastRequest?.correlationId?.slice(0, 18) ?? '—'}</span></span>
      </footer>
    </div>
  )
}
