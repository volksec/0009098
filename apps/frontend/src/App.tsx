import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  api, clearSession, currentUser, onRequest, probeCrossTenant, setUnauthorizedHandler,
  type DashboardSummary, type Invariant, type LastRequest, type Policy,
  type RlsPolicy, type SchemaStats, type SessionUser,
} from './api'
import { Login } from './Login'
import { CustomerAdmin } from './CustomerAdmin'
import { BillingPage, ClaimsPage, CommissionsPage } from './Operations'
import { LiveConsole } from './LiveConsole'
import { ProposalsPage, QuotationsPage } from './Underwriting'

type Page =
  | 'dashboard' | 'admin' | 'quotations' | 'proposals' | 'policies'
  | 'billing' | 'commissions' | 'claims'
  | 'console' | 'engineering' | 'isolation'

const PAGES: { id: Page; label: string; group: string }[] = [
  { id: 'dashboard', label: 'Painel', group: 'Operação' },
  { id: 'admin', label: 'Clientes', group: 'Operação' },
  { id: 'quotations', label: 'Cotações', group: 'Operação' },
  { id: 'proposals', label: 'Propostas', group: 'Operação' },
  { id: 'policies', label: 'Apólices', group: 'Operação' },
  { id: 'billing', label: 'Faturamento', group: 'Operação' },
  { id: 'commissions', label: 'Comissões', group: 'Operação' },
  { id: 'claims', label: 'Sinistros', group: 'Operação' },
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

function DashboardPage() {
  const { data, loading, error } = useAsync<DashboardSummary>(
    () => api.dashboard(), [])

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

function PoliciesPage() {
  const { data, loading, error } = useAsync<Policy[]>(() => api.policies(), [])

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

/**
 * Demonstração de isolamento, agora que o tenant sai do token.
 *
 * Antes a página buscava um cliente de outra corretora e forjava o cabeçalho de tenant.
 * Com a autenticação isso deixou de ser possível — e a demonstração ficou mais forte:
 * quem avalia entra como corretor de uma corretora, copia um identificador real, sai,
 * entra como corretor de outra e cola aqui. O recurso existe e mesmo assim some.
 */
function IsolationPage({ user }: { user: SessionUser }) {
  const [id, setId] = useState('')
  const [result, setResult] = useState<{ status: number; durationMs: number; id: string } | null>(null)
  const [running, setRunning] = useState(false)

  const run = useCallback(async () => {
    const alvo = id.trim()
    if (!alvo) return

    setRunning(true)
    setResult(null)
    try {
      const probe = await probeCrossTenant(alvo)
      setResult({ status: probe.status, durationMs: probe.durationMs, id: alvo })
    } finally {
      setRunning(false)
    }
  }, [id])

  return (
    <Panel
      title="Isolamento entre corretoras"
      subtitle="Busca um cliente pelo identificador exato, com o token da sessão atual"
    >
      <div style={{ padding: 16 }}>
        <p style={{ marginTop: 0, fontSize: 13.5, color: 'var(--pdc-slate-700)' }}>
          Você está autenticado como <strong>{user.name}</strong>, da{' '}
          <strong>{user.tenantName}</strong>. Para reproduzir o cenário IDOR — em que o atacante
          já conhece o identificador do recurso:
        </p>

        <ol style={{ fontSize: 13.5, color: 'var(--pdc-slate-700)', lineHeight: 1.7 }}>
          <li>Abra <strong>Clientes</strong> e copie o identificador de um cliente desta corretora.</li>
          <li>Cole abaixo e execute: deve responder <strong>200</strong>, porque é seu.</li>
          <li>Saia, entre com um corretor de <strong>outra</strong> corretora e cole o mesmo
              identificador: o mesmo recurso passa a responder <strong>404</strong>.</li>
        </ol>

        <div style={{ display: 'flex', gap: 10, alignItems: 'center', flexWrap: 'wrap' }}>
          <input
            className="search"
            style={{ flex: 1, minWidth: 320 }}
            placeholder="identificador do cliente (UUID)"
            value={id}
            onChange={(event) => setId(event.target.value)}
          />
          <button className="btn" onClick={run} disabled={running || !id.trim()}>
            {running ? 'Executando…' : 'Buscar'}
          </button>
        </div>

        {result && (
          <div className={`result-box ${result.status === 404 ? 'blocked' : 'allowed'}`}>
            {result.status === 404 ? (
              <>
                <strong>Bloqueado — HTTP 404</strong> em {result.durationMs} ms.<br />
                Se este identificador veio de outra corretora, ele existe no banco: a
                Row-Level Security é que o torna invisível para o tenant do seu token, então a
                consulta retorna zero linhas e a API responde 404.<br />
                <br />
                A resposta é <strong>404 e não 403</strong> de propósito: um 403 confirmaria que o
                recurso existe, transformando o controle de acesso em oráculo de enumeração.
              </>
            ) : result.status === 200 ? (
              <>
                <strong>Encontrado — HTTP 200</strong> em {result.durationMs} ms. Este cliente
                pertence à sua corretora. Repita o passo 3 entrando por outra para ver o mesmo
                identificador desaparecer.
              </>
            ) : (
              <>
                <strong>HTTP {result.status}</strong> — resposta inesperada para este cenário.
              </>
            )}
          </div>
        )}

        <div className="note">
          O tenant sai do <strong>claim do token assinado</strong>: não há cabeçalho a forjar.
          Trocar de corretora exige entrar com um usuário dela. Esse é o comportamento da
          camada 5 (RLS) sob a camada 1 (autenticação) — as demais são verificadas na suíte
          de integração.
        </div>
      </div>
    </Panel>
  )
}

// ---------------------------------------------------------------- shell

export default function App() {
  const [page, setPage] = useState<Page>('dashboard')
  const [user, setUser] = useState<SessionUser | null>(() => currentUser())
  const [lastRequest, setLastRequest] = useState<LastRequest | null>(null)

  useEffect(() => onRequest(setLastRequest), [])

  // Token expirado em qualquer chamada devolve à tela de entrada, sem tela travada
  useEffect(() => setUnauthorizedHandler(() => setUser(null)), [])

  const sair = () => {
    clearSession()
    setUser(null)
    setPage('dashboard')
  }

  if (!user) return <Login onEntrar={setUser} />

  const heading: Record<Page, { title: string; subtitle: string }> = {
    dashboard: { title: 'Painel do corretor', subtitle: 'Indicadores da carteira do corretor autenticado' },
    admin: {
      title: 'Administração de clientes',
      subtitle: 'Cadastro, edição e exclusão lógica persistidos diretamente no PostgreSQL',
    },
    quotations: {
      title: 'Cotações',
      subtitle: 'Assistente de cotação e comparação dos três planos calculados',
    },
    proposals: {
      title: 'Propostas',
      subtitle: 'Análise de risco versionada e emissão de apólice',
    },
    policies: { title: 'Apólices', subtitle: 'Contratos emitidos e suas vigências' },
    billing: {
      title: 'Faturamento',
      subtitle: 'Parcelas, inadimplência e pagamento simulado',
    },
    commissions: {
      title: 'Comissões',
      subtitle: 'Extrato por corretor, consolidação mensal, liberação e estorno',
    },
    claims: {
      title: 'Sinistros',
      subtitle: 'Aviso, linha do tempo append-only e decisão simulada',
    },
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
          {PAGES.map((item, index) => (
            <div key={item.id}>
              {/* Cabeçalho aparece só na primeira entrada de cada grupo */}
              {(index === 0 || PAGES[index - 1].group !== item.group) && (
                <div className="nav-group">{item.group}</div>
              )}
              <button
                aria-current={page === item.id}
                onClick={() => setPage(item.id)}
              >
                {item.label}
              </button>
            </div>
          ))}
        </nav>

        <div className="tenant-picker">
          <label>Sessão</label>
          <div className="session-card">
            <div className="session-name">{user.name}</div>
            <div className="session-tenant">{user.tenantName}</div>
          </div>
          <button className="btn ghost small" onClick={sair} style={{ width: '100%' }}>
            Sair
          </button>
        </div>
      </aside>

      <main className="content">
        <div className="page-head">
          <h1>{heading[page].title}</h1>
          <p>
            {heading[page].subtitle}
            {page !== 'engineering' && <> · <strong>{user.tenantName}</strong></>}
          </p>
        </div>

        {page === 'dashboard' && <DashboardPage />}
        {page === 'admin' && <CustomerAdmin />}
        {page === 'billing' && <BillingPage />}
        {page === 'commissions' && <CommissionsPage />}
        {page === 'claims' && <ClaimsPage />}
        {page === 'console' && <LiveConsole />}
        {page === 'quotations' && <QuotationsPage />}
        {page === 'proposals' && <ProposalsPage />}
        {page === 'policies' && <PoliciesPage />}
        {page === 'engineering' && <EngineeringPage />}
        {page === 'isolation' && <IsolationPage user={user} />}
      </main>

      <footer className="trace-bar">
        <span><span className="k">sessão</span> <span className="v mono">{user.name}</span></span>
        <span><span className="k">rota</span> <span className="v mono">{lastRequest?.path ?? '—'}</span></span>
        <span><span className="k">status</span> <span className="v mono">{lastRequest?.status ?? '—'}</span></span>
        <span><span className="k">duração</span> <span className="v mono">{lastRequest ? `${lastRequest.durationMs} ms` : '—'}</span></span>
        <span><span className="k">correlation-id</span> <span className="v mono">{lastRequest?.correlationId?.slice(0, 18) ?? '—'}</span></span>
      </footer>
    </div>
  )
}
