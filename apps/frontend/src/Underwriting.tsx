import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  ApiError, api, proposalApi, quotationApi,
  type Broker, type CoverageOption, type Customer, type InsurableAsset,
  type PagedResult, type ProductVersion, type ProposalDetail, type ProposalSummary,
  type QuotationDetail, type QuotationSummary,
} from './api'

const money = (value: number) =>
  new Intl.NumberFormat('pt-BR', { style: 'currency', currency: 'BRL' }).format(value)

/** O banco guarda a franquia percentual como fração (0,05 = 5%). */
const percent = (fraction: number) =>
  new Intl.NumberFormat('pt-BR', { style: 'percent', maximumFractionDigits: 2 }).format(fraction)

const decimals = (value: number, digits: number) =>
  new Intl.NumberFormat('pt-BR', { minimumFractionDigits: digits, maximumFractionDigits: digits })
    .format(value)

const shortDate = (value: string) =>
  new Date(value).toLocaleDateString('pt-BR', { day: '2-digit', month: '2-digit', year: 'numeric' })

const dateTime = (value: string) =>
  new Date(value).toLocaleString('pt-BR', {
    day: '2-digit', month: '2-digit', year: '2-digit', hour: '2-digit', minute: '2-digit',
  })

type Toast = { id: number; tone: 'ok' | 'error'; message: string }

function useToasts() {
  const [toasts, setToasts] = useState<Toast[]>([])

  const notify = useCallback((tone: Toast['tone'], message: string) => {
    const id = Date.now() + Math.random()
    setToasts((current) => [...current, { id, tone, message }])
    setTimeout(() => setToasts((current) => current.filter((t) => t.id !== id)), 5000)
  }, [])

  const view = (
    <div className="toasts">
      {toasts.map((toast) => (
        <div key={toast.id} className={`toast ${toast.tone}`}>{toast.message}</div>
      ))}
    </div>
  )

  return { notify, view }
}

const PLAN_LABEL: Record<string, string> = {
  ESSENTIAL: 'Essencial', COMPLETE: 'Completo', MASTER: 'Master',
}

const RISK_TONE: Record<string, string> = {
  LOW: 'ok', MODERATE: 'info', HIGH: 'warn', SEVERE: 'danger',
}

const USAGE_LABEL: Record<string, string> = {
  PERSONAL: 'Particular',
  COMMUTE: 'Deslocamento casa–trabalho',
  COMMERCIAL: 'Comercial',
  RIDESHARE: 'Aplicativo de transporte',
}

// ================================================================ assistente de cotação

type WizardStep = 0 | 1 | 2 | 3

interface WizardState {
  customerId: string
  assetId: string
  productVersionId: string
  coverageIds: string[]
  hasGarage: boolean
  usage: string
  driverAge: number
  previousClaims: boolean
}

const INITIAL: WizardState = {
  customerId: '', assetId: '', productVersionId: '', coverageIds: [],
  hasGarage: true, usage: 'PERSONAL', driverAge: 35, previousClaims: false,
}

/** Ramo do produto compatível com o tipo de bem — evita cotar imóvel em produto de auto. */
const BRANCH_FOR_ASSET: Record<string, string> = { VEHICLE: 'AUTO', PROPERTY: 'RESIDENTIAL' }

function QuotationWizard({ tenantId, actorId, brokerId, brokerName, onDone, onCancel }: {
  tenantId: string
  actorId: string
  brokerId: string
  brokerName: string
  onDone: (quotationId: string) => void
  onCancel: () => void
}) {
  const [step, setStep] = useState<WizardStep>(0)
  const [form, setForm] = useState<WizardState>(INITIAL)
  const [customers, setCustomers] = useState<Customer[]>([])
  const [search, setSearch] = useState('')
  const [assets, setAssets] = useState<InsurableAsset[]>([])
  const [catalog, setCatalog] = useState<{ products: ProductVersion[]; coverages: CoverageOption[] } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  useEffect(() => { quotationApi.catalog(tenantId).then(setCatalog).catch(() => setCatalog(null)) },
    [tenantId])

  useEffect(() => {
    const handle = setTimeout(() => {
      // Só a carteira do corretor selecionado: a proposta herda o corretor do cliente,
      // então cotar cliente de outra carteira produziria uma proposta que este corretor
      // não poderia emitir.
      api.customers(tenantId, { search: search || undefined, status: 'ACTIVE', brokerId, pageSize: 12 })
        .then((result: PagedResult<Customer>) => setCustomers(result.items))
        .catch(() => setCustomers([]))
    }, 220)
    return () => clearTimeout(handle)
  }, [tenantId, search, brokerId])

  const selectCustomer = async (customer: Customer) => {
    setForm({ ...INITIAL, customerId: customer.id })
    setAssets([])
    const list = await quotationApi.assets(tenantId, customer.id)
    setAssets(list)
    setStep(1)
  }

  const asset = assets.find((a) => a.id === form.assetId)

  // O catálogo é filtrado pelo ramo do bem escolhido
  const products = useMemo(() => {
    if (!catalog || !asset) return []
    const branch = BRANCH_FOR_ASSET[asset.kind]
    return catalog.products.filter((p) => p.branch === branch)
  }, [catalog, asset])

  const coverages = useMemo(
    () => catalog?.coverages.filter((c) => c.productVersionId === form.productVersionId) ?? [],
    [catalog, form.productVersionId])

  const selectProduct = (productVersionId: string) => {
    // Coberturas obrigatórias já entram marcadas — desmarcá-las é bloqueado pelo domínio
    const mandatory = (catalog?.coverages ?? [])
      .filter((c) => c.productVersionId === productVersionId && c.isMandatory)
      .map((c) => c.id)
    setForm((f) => ({ ...f, productVersionId, coverageIds: mandatory }))
  }

  const toggleCoverage = (coverage: CoverageOption) => {
    if (coverage.isMandatory) return
    setForm((f) => ({
      ...f,
      coverageIds: f.coverageIds.includes(coverage.id)
        ? f.coverageIds.filter((id) => id !== coverage.id)
        : [...f.coverageIds, coverage.id],
    }))
  }

  const submit = async () => {
    setSubmitting(true)
    setError(null)
    try {
      const result = await quotationApi.create(tenantId, actorId, {
        customerId: form.customerId,
        assetId: form.assetId,
        productVersionId: form.productVersionId,
        coverageIds: form.coverageIds,
        hasGarage: form.hasGarage,
        usage: form.usage,
        driverAge: form.driverAge,
        previousClaims: form.previousClaims,
      })
      onDone(result.id)
    } catch (err) {
      const api = err as ApiError
      setError(api.message)
    } finally {
      setSubmitting(false)
    }
  }

  const customer = customers.find((c) => c.id === form.customerId)
  const steps = ['Cliente', 'Bem e produto', 'Coberturas', 'Perfil de risco']

  return (
    <div className="modal-backdrop" onClick={onCancel}>
      <div className="modal wide" onClick={(e) => e.stopPropagation()}>
        <header className="modal-head">
          <div>
            <h3>Nova cotação</h3>
            <div className="sub">Emitida por {brokerName}</div>
          </div>
          <button className="icon-btn" onClick={onCancel} aria-label="Fechar">×</button>
        </header>

        <div className="stepper">
          {steps.map((label, index) => (
            <div key={label} className={`step ${index === step ? 'current' : index < step ? 'done' : ''}`}>
              <span className="step-num">{index < step ? '✓' : index + 1}</span>
              {label}
            </div>
          ))}
        </div>

        <div className="modal-body">
          {error && <div className="alert">{error}</div>}

          {/* ---------------------------------------------------------- 1. cliente */}
          {step === 0 && (
            <>
              <input
                className="search"
                autoFocus
                placeholder="Buscar cliente por nome ou documento…"
                value={search}
                onChange={(e) => setSearch(e.target.value)}
                style={{ width: '100%', marginBottom: 12 }}
              />
              {customers.length === 0 && <div className="state">Nenhum cliente encontrado.</div>}
              <div className="pick-list">
                {customers.map((c) => (
                  <button key={c.id} className="pick" onClick={() => selectCustomer(c)}>
                    <div>
                      <strong>{c.displayName}</strong>
                      <div className="hint-text">
                        {c.kind === 'INDIVIDUAL' ? 'Pessoa física' : 'Pessoa jurídica'} ·{' '}
                        {c.assetCount} bem(ns) · {c.activePolicies} apólice(s) ativa(s)
                      </div>
                    </div>
                    <span className="badge muted">selecionar</span>
                  </button>
                ))}
              </div>
            </>
          )}

          {/* ------------------------------------------------- 2. bem e produto */}
          {step === 1 && (
            <>
              <div className="detail-label">Cliente</div>
              <p style={{ marginTop: 2 }}><strong>{customer?.displayName}</strong></p>

              <div className="detail-label" style={{ marginTop: 14 }}>Bem segurado</div>
              {assets.length === 0 && (
                <div className="state">Este cliente não possui bens cadastrados.</div>
              )}
              <div className="pick-list">
                {assets.map((a) => (
                  <button
                    key={a.id}
                    className={`pick ${form.assetId === a.id ? 'selected' : ''}`}
                    onClick={() => setForm((f) => ({ ...f, assetId: a.id, productVersionId: '', coverageIds: [] }))}
                  >
                    <div>
                      <strong>{a.label}</strong>
                      <div className="hint-text">
                        Valor declarado {money(a.declaredValue)} ·{' '}
                        {a.kind === 'VEHICLE' ? 'veículo' : 'imóvel'}
                      </div>
                    </div>
                    {form.assetId === a.id && <span className="badge ok">escolhido</span>}
                  </button>
                ))}
              </div>

              {form.assetId && (
                <>
                  <div className="detail-label" style={{ marginTop: 16 }}>Produto</div>
                  {products.length === 0 && (
                    <div className="state">Nenhum produto disponível para este tipo de bem.</div>
                  )}
                  <div className="pick-list">
                    {products.map((p) => (
                      <button
                        key={p.id}
                        className={`pick ${form.productVersionId === p.id ? 'selected' : ''}`}
                        onClick={() => selectProduct(p.id)}
                      >
                        <div>
                          <strong>{p.name}</strong>
                          <div className="hint-text">
                            versão {p.version} · aceita risco até {p.maxAcceptableRisk} ·
                            importância entre {money(p.minInsuredValue)} e {money(p.maxInsuredValue)}
                          </div>
                        </div>
                        {form.productVersionId === p.id && <span className="badge ok">escolhido</span>}
                      </button>
                    ))}
                  </div>
                </>
              )}
            </>
          )}

          {/* ---------------------------------------------------- 3. coberturas */}
          {step === 2 && (
            <>
              <div className="note">
                Coberturas obrigatórias vêm marcadas e não podem ser removidas. A regra não é
                da tela: quem recusa é o domínio, e a API responde{' '}
                <code>MANDATORY_COVERAGE_MISSING</code> mesmo que a requisição venha por fora.
              </div>
              <div className="coverage-list">
                {coverages.map((c) => {
                  const checked = form.coverageIds.includes(c.id)
                  return (
                    <label
                      key={c.id}
                      className={`coverage ${checked ? 'on' : ''} ${c.isMandatory ? 'locked' : ''}`}
                    >
                      <input
                        type="checkbox"
                        checked={checked}
                        disabled={c.isMandatory}
                        onChange={() => toggleCoverage(c)}
                      />
                      <div>
                        <strong>{c.name}</strong>
                        {c.isMandatory && <span className="badge warn" style={{ marginLeft: 8 }}>obrigatória</span>}
                        <div className="hint-text">{c.description}</div>
                        <div className="hint-text">
                          Limite entre {money(c.minLimit)} e {money(c.maxLimit)} · franquia{' '}
                          {c.deductibleKind === 'PERCENTAGE'
                            ? percent(c.deductiblePercent ?? 0)
                            : money(c.deductibleAmount ?? 0)}
                        </div>
                      </div>
                    </label>
                  )
                })}
              </div>
            </>
          )}

          {/* ------------------------------------------------- 4. questionário */}
          {step === 3 && (
            <>
              <div className="note">
                As respostas alimentam um cálculo <strong>determinístico</strong>: as mesmas
                entradas produzem sempre o mesmo prêmio. Os fatores usados ficam gravados junto
                com a cotação, então o valor ofertado pode ser reproduzido meses depois.
              </div>

              <div className="field-row">
                <div className="field">
                  <label htmlFor="usage">Uso do bem</label>
                  <select
                    id="usage"
                    value={form.usage}
                    onChange={(e) => setForm((f) => ({ ...f, usage: e.target.value }))}
                  >
                    {Object.entries(USAGE_LABEL).map(([value, label]) => (
                      <option key={value} value={value}>{label}</option>
                    ))}
                  </select>
                </div>
                <div className="field">
                  <label htmlFor="age">Idade do condutor principal</label>
                  <input
                    id="age"
                    type="number"
                    min={18}
                    max={99}
                    value={form.driverAge}
                    onChange={(e) => setForm((f) => ({ ...f, driverAge: Number(e.target.value) }))}
                  />
                </div>
              </div>

              <label className="coverage" style={{ marginTop: 10 }}>
                <input
                  type="checkbox"
                  checked={form.hasGarage}
                  onChange={(e) => setForm((f) => ({ ...f, hasGarage: e.target.checked }))}
                />
                <div>
                  <strong>Pernoite em garagem fechada</strong>
                  <div className="hint-text">Reduz o escore de risco em 90 pontos</div>
                </div>
              </label>

              <label className="coverage">
                <input
                  type="checkbox"
                  checked={form.previousClaims}
                  onChange={(e) => setForm((f) => ({ ...f, previousClaims: e.target.checked }))}
                />
                <div>
                  <strong>Sinistros nos últimos 24 meses</strong>
                  <div className="hint-text">Acrescenta 130 pontos ao escore</div>
                </div>
              </label>
            </>
          )}
        </div>

        <footer className="modal-foot">
          <button className="btn ghost" onClick={() => (step === 0 ? onCancel() : setStep((s) => (s - 1) as WizardStep))}>
            {step === 0 ? 'Cancelar' : 'Voltar'}
          </button>
          {step < 3 ? (
            <button
              className="btn"
              disabled={
                (step === 0 && !form.customerId) ||
                (step === 1 && (!form.assetId || !form.productVersionId)) ||
                (step === 2 && form.coverageIds.length === 0)
              }
              onClick={() => setStep((s) => (s + 1) as WizardStep)}
            >
              Avançar
            </button>
          ) : (
            <button className="btn" disabled={submitting} onClick={submit}>
              {submitting ? 'Calculando…' : 'Calcular os três planos'}
            </button>
          )}
        </footer>
      </div>
    </div>
  )
}

// ================================================================ comparação de planos

function QuotationDetailView({ tenantId, actorId, id, onConverted, onClose }: {
  tenantId: string
  actorId: string
  id: string
  onConverted: (proposalId: string) => void
  onClose: () => void
}) {
  const [data, setData] = useState<QuotationDetail | null>(null)
  const [chosen, setChosen] = useState<string>('COMPLETE')
  const [installments, setInstallments] = useState(4)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    quotationApi.detail(tenantId, id)
      .then((detail) => {
        setData(detail)
        if (detail.plans.length > 0 && !detail.plans.some((p) => p.plan === 'COMPLETE'))
          setChosen(detail.plans[0].plan)
      })
      .catch((err: ApiError) => setError(err.message))
  }, [tenantId, id])

  const convert = async () => {
    setBusy(true)
    setError(null)
    try {
      const proposal = await quotationApi.convert(tenantId, actorId, id, chosen, installments)
      onConverted(proposal.id)
    } catch (err) {
      setError((err as ApiError).message)
    } finally {
      setBusy(false)
    }
  }

  if (error && !data) return <div className="alert">{error}</div>
  if (!data) return <div className="state">Carregando cotação…</div>

  const { quotation, plans, coverages } = data
  const rejected = quotation.status === 'REJECTED'
  const convertible = quotation.status === 'CALCULATED' && !quotation.isExpired && !quotation.hasProposal

  return (
    <>
      <div className="panel">
        <header className="panel-head">
          <div>
            <h2 className="mono">{quotation.number}</h2>
            <div className="sub">
              {quotation.customerName} · {quotation.productName} · importância segurada{' '}
              {money(quotation.insuredValue)}
            </div>
          </div>
          <button className="btn ghost" onClick={onClose}>Voltar à lista</button>
        </header>

        <div className="grid" style={{ padding: 16 }}>
          <div className="card">
            <div className="label">Escore de risco</div>
            <div className="value">{quotation.riskScore}</div>
            <div className="hint">
              <span className={`badge ${RISK_TONE[quotation.riskBand] ?? 'info'}`}>
                {quotation.riskBand}
              </span>
            </div>
          </div>
          <div className="card">
            <div className="label">Situação</div>
            <div className="value" style={{ fontSize: 22 }}>{quotation.status}</div>
            <div className="hint">
              {quotation.isExpired ? 'prazo de validade vencido' : `válida até ${shortDate(quotation.expiresAt)}`}
            </div>
          </div>
          <div className="card">
            <div className="label">Motor de cálculo</div>
            <div className="value" style={{ fontSize: 22 }}>
              v{plans[0]?.engineVersion ?? '—'}
            </div>
            <div className="hint">versão gravada no snapshot</div>
          </div>
        </div>
      </div>

      {rejected && (
        <div className="alert">
          <strong>Cotação recusada.</strong>{' '}
          {(quotation.rejectionReasons ?? []).join(' ')}
          <br /><br />
          A recusa foi <strong>persistida</strong>, não descartada: o registro fica disponível
          para auditoria e para a consulta regulatória, com o motivo que a produziu.
        </div>
      )}

      {plans.length > 0 && (
        <>
          <div className="plan-grid">
            {plans.map((plan) => {
              const planCoverages = coverages.filter((c) => c.planId === plan.id)
              const selected = chosen === plan.plan
              return (
                <button
                  key={plan.id}
                  className={`plan ${selected ? 'selected' : ''} ${plan.plan === 'COMPLETE' ? 'featured' : ''}`}
                  onClick={() => setChosen(plan.plan)}
                  disabled={!convertible}
                >
                  {plan.plan === 'COMPLETE' && <div className="plan-tag">mais escolhido</div>}
                  <div className="plan-name">{PLAN_LABEL[plan.plan] ?? plan.plan}</div>
                  <div className="plan-price">{money(plan.totalPremium)}</div>
                  <div className="plan-net">
                    prêmio líquido {money(plan.netPremium)} + carregamento
                  </div>

                  <ul className="plan-covers">
                    {planCoverages.map((c) => (
                      <li key={`${plan.id}-${c.code}`}>
                        <span>{c.name}</span>
                        <span className="mono">{money(c.limit)}</span>
                      </li>
                    ))}
                  </ul>

                  <div className="plan-foot">
                    multiplicador de plano {decimals(plan.planMultiplier, 2)}× · de risco{' '}
                    {decimals(plan.riskMultiplier, 4)}×
                  </div>
                </button>
              )
            })}
          </div>

          <div className="note">
            Os três planos partem do mesmo escore de risco e das mesmas coberturas — o que muda é
            o <strong>fator de limite</strong> (0,70× / 1,00× / 1,40× do valor do bem) e o
            multiplicador de prêmio. Cada plano guarda seu próprio snapshot com todos os fatores
            de entrada, que é o que torna o cálculo reproduzível.
          </div>

          {error && <div className="alert">{error}</div>}

          <div className="panel">
            <header className="panel-head">
              <div>
                <h2>Converter em proposta</h2>
                <div className="sub">
                  {convertible
                    ? 'A conversão cria a proposta e move a cotação para CONVERTED, na mesma transação'
                    : quotation.hasProposal
                      ? 'Esta cotação já foi convertida — um índice único parcial impede a segunda proposta'
                      : 'Cotação expirada ou em status que não permite conversão'}
                </div>
              </div>
            </header>

            <div style={{ padding: 16, display: 'flex', gap: 12, alignItems: 'flex-end', flexWrap: 'wrap' }}>
              <div className="field">
                <label htmlFor="plan">Plano escolhido</label>
                <select id="plan" value={chosen} disabled={!convertible}
                        onChange={(e) => setChosen(e.target.value)}>
                  {plans.map((p) => (
                    <option key={p.plan} value={p.plan}>
                      {PLAN_LABEL[p.plan]} — {money(p.totalPremium)}
                    </option>
                  ))}
                </select>
              </div>
              <div className="field">
                <label htmlFor="inst">Parcelamento</label>
                <select id="inst" value={installments} disabled={!convertible}
                        onChange={(e) => setInstallments(Number(e.target.value))}>
                  {[1, 2, 3, 4, 6, 10, 12].map((n) => (
                    <option key={n} value={n}>{n}× </option>
                  ))}
                </select>
              </div>
              <button className="btn" disabled={!convertible || busy} onClick={convert}>
                {busy ? 'Convertendo…' : 'Gerar proposta'}
              </button>
            </div>
          </div>
        </>
      )}
    </>
  )
}

// ================================================================ página de cotações

export function QuotationsPage({ tenantId }: { tenantId: string }) {
  const [brokers, setBrokers] = useState<Broker[]>([])
  const [actorId, setActorId] = useState('')
  const [data, setData] = useState<PagedResult<QuotationSummary> | null>(null)
  const [status, setStatus] = useState('')
  const [page, setPage] = useState(1)
  const [loading, setLoading] = useState(true)
  const [wizard, setWizard] = useState(false)
  const [openId, setOpenId] = useState<string | null>(null)
  const { notify, view: toastView } = useToasts()

  useEffect(() => {
    api.brokers(tenantId).then((list) => {
      setBrokers(list)
      setActorId(list[0]?.userId ?? '')
    })
  }, [tenantId])

  const load = useCallback(async () => {
    setLoading(true)
    try {
      setData(await quotationApi.list(tenantId, { status: status || undefined, page }))
    } finally {
      setLoading(false)
    }
  }, [tenantId, status, page])

  useEffect(() => { void load() }, [load])

  if (openId) {
    return (
      <>
        {toastView}
        <QuotationDetailView
          tenantId={tenantId}
          actorId={actorId}
          id={openId}
          onClose={() => { setOpenId(null); void load() }}
          onConverted={() => {
            notify('ok', 'Proposta criada. Ela aparece agora na aba Propostas para análise.')
            setOpenId(null)
            void load()
          }}
        />
      </>
    )
  }

  return (
    <>
      {toastView}

      <div className="filters">
        <select
          className="search"
          value={actorId}
          onChange={(e) => setActorId(e.target.value)}
          aria-label="Corretor responsável"
        >
          {brokers.map((b) => (
            <option key={b.userId} value={b.userId}>{b.fullName}</option>
          ))}
        </select>

        <select className="search" value={status}
                onChange={(e) => { setStatus(e.target.value); setPage(1) }}>
          <option value="">Todos os status</option>
          <option value="CALCULATED">Calculadas</option>
          <option value="CONVERTED">Convertidas</option>
          <option value="REJECTED">Recusadas</option>
          <option value="EXPIRED">Expiradas</option>
        </select>

        <button className="btn" disabled={!actorId} onClick={() => setWizard(true)}>
          Nova cotação
        </button>

        {data && <span className="filter-count">{data.total} registro(s)</span>}
      </div>

      <section className="panel">
        <header className="panel-head">
          <div>
            <h2>Cotações</h2>
            <div className="sub">
              Cada cotação guarda os três planos calculados e o snapshot dos fatores de entrada
            </div>
          </div>
        </header>

        {loading && <div className="state">Carregando…</div>}
        {!loading && data?.items.length === 0 && (
          <div className="state">Nenhuma cotação com este filtro.</div>
        )}

        {!loading && data && data.items.length > 0 && (
          <table>
            <thead>
              <tr>
                <th>Número</th><th>Cliente</th><th>Produto</th>
                <th className="num">Risco</th><th className="num">A partir de</th>
                <th>Validade</th><th>Status</th><th />
              </tr>
            </thead>
            <tbody>
              {data.items.map((q) => (
                <tr key={q.id}>
                  <td className="mono">{q.number}</td>
                  <td>{q.customerName}</td>
                  <td>{q.productName}</td>
                  <td className="num">
                    <span className={`badge ${RISK_TONE[q.riskBand] ?? 'info'}`}>
                      {q.riskScore}
                    </span>
                  </td>
                  <td className="num">{q.fromPremium ? money(q.fromPremium) : '—'}</td>
                  <td className={q.isExpired ? 'error-text' : undefined}>
                    {shortDate(q.expiresAt)}
                  </td>
                  <td>
                    <span className={`badge ${
                      q.status === 'CONVERTED' ? 'ok' :
                      q.status === 'REJECTED' ? 'danger' :
                      q.isExpired ? 'warn' : 'info'}`}>
                      {q.status}
                    </span>
                  </td>
                  <td>
                    <button className="btn ghost small" onClick={() => setOpenId(q.id)}>
                      Abrir
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}

        {data && data.total > data.pageSize && (
          <div className="pagination">
            <button className="btn ghost" disabled={page === 1} onClick={() => setPage((p) => p - 1)}>
              Anterior
            </button>
            <span>página {page} de {Math.ceil(data.total / data.pageSize)}</span>
            <button
              className="btn ghost"
              disabled={page >= Math.ceil(data.total / data.pageSize)}
              onClick={() => setPage((p) => p + 1)}
            >
              Próxima
            </button>
          </div>
        )}
      </section>

      {wizard && actorId && (
        <QuotationWizard
          tenantId={tenantId}
          actorId={actorId}
          brokerId={brokers.find((b) => b.userId === actorId)?.id ?? ''}
          brokerName={brokers.find((b) => b.userId === actorId)?.fullName ?? ''}
          onCancel={() => setWizard(false)}
          onDone={(id) => {
            setWizard(false)
            setOpenId(id)
            notify('ok', 'Cotação calculada. Compare os três planos abaixo.')
          }}
        />
      )}
    </>
  )
}

// ================================================================ propostas

function ProposalDetailView({ tenantId, actorId, id, onClose, notify }: {
  tenantId: string
  actorId: string
  id: string
  onClose: () => void
  notify: (tone: 'ok' | 'error', message: string) => void
}) {
  const [data, setData] = useState<ProposalDetail | null>(null)
  const [outcome, setOutcome] = useState('APPROVED')
  const [reason, setReason] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // A chave nasce quando a proposta é aberta e sobrevive a novos cliques: reenviá-la
  // devolve a resposta original em vez de emitir uma segunda apólice.
  const [idempotencyKey] = useState(() => crypto.randomUUID())
  const inFlight = useRef(false)

  const load = useCallback(async () => {
    setData(await proposalApi.detail(tenantId, id))
  }, [tenantId, id])

  useEffect(() => { void load() }, [load])

  const decide = async () => {
    setBusy(true); setError(null)
    try {
      const result = await proposalApi.underwrite(tenantId, actorId, id, outcome, reason)
      notify('ok', `Decisão versão ${result.version} registrada — proposta em ${result.status}.`)
      setReason('')
      await load()
    } catch (err) {
      setError((err as ApiError).message)
    } finally {
      setBusy(false)
    }
  }

  const issue = async () => {
    // Guarda síncrona: `busy` só desabilita o botão no próximo render, e dois cliques
    // rápidos passam antes disso. O servidor barra de qualquer forma — mas com o lock
    // otimista, o que o usuário veria seria um conflito, não a emissão bem-sucedida.
    if (inFlight.current) return
    inFlight.current = true
    setBusy(true); setError(null)
    try {
      const policy = await proposalApi.issue(tenantId, actorId, id, idempotencyKey)
      notify('ok', `Apólice ${policy.number} emitida — ${policy.installments} parcela(s).`)
      await load()
    } catch (err) {
      setError((err as ApiError).message)
    } finally {
      inFlight.current = false
      setBusy(false)
    }
  }

  if (!data) return <div className="state">Carregando proposta…</div>

  const p = data.proposal
  const canDecide = p.status === 'SUBMITTED' || p.status === 'UNDER_ANALYSIS'
  const canIssue = p.status === 'APPROVED' && p.openPendencies === 0

  return (
    <>
      <div className="panel">
        <header className="panel-head">
          <div>
            <h2 className="mono">{p.number}</h2>
            <div className="sub">
              {p.customerName} · {p.productName} · plano {PLAN_LABEL[p.chosenPlan] ?? p.chosenPlan} ·
              origem {p.quotationNumber}
            </div>
          </div>
          <button className="btn ghost" onClick={onClose}>Voltar à lista</button>
        </header>

        <div className="grid" style={{ padding: 16 }}>
          <div className="card">
            <div className="label">Prêmio total</div>
            <div className="value">{money(p.totalPremium)}</div>
            <div className="hint">líquido {money(p.netPremium)} · {p.installmentCount}×</div>
          </div>
          <div className="card">
            <div className="label">Escore de risco</div>
            <div className="value">{p.riskScore}</div>
            <div className="hint">
              <span className={`badge ${RISK_TONE[p.riskBand] ?? 'info'}`}>{p.riskBand}</span>
            </div>
          </div>
          <div className="card">
            <div className="label">Situação</div>
            <div className="value" style={{ fontSize: 22 }}>{p.status}</div>
            <div className="hint">
              {p.policyNumber ? <>apólice <span className="mono">{p.policyNumber}</span></> : '—'}
            </div>
          </div>
        </div>
      </div>

      {error && <div className="alert">{error}</div>}

      <div className="panel">
        <header className="panel-head">
          <div>
            <h2>Análise de risco</h2>
            <div className="sub">
              Decisões são versionadas e imutáveis — uma nova análise acrescenta a versão
              seguinte, sem apagar a anterior
            </div>
          </div>
        </header>

        {data.decisions.length === 0 && (
          <div className="state">Nenhuma decisão registrada.</div>
        )}
        {data.decisions.length > 0 && (
          <table>
            <thead><tr><th>Versão</th><th>Resultado</th><th>Motivos</th><th>Data</th></tr></thead>
            <tbody>
              {data.decisions.map((d) => (
                <tr key={d.version}>
                  <td className="mono">v{d.version}</td>
                  <td>
                    <span className={`badge ${
                      d.outcome === 'APPROVED' ? 'ok' :
                      d.outcome === 'REJECTED' ? 'danger' : 'warn'}`}>
                      {d.outcome}
                    </span>
                  </td>
                  <td>{d.reasons.join(' · ')}</td>
                  <td>{dateTime(d.decidedAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}

        {canDecide && (
          <div style={{ padding: 16, display: 'flex', gap: 12, alignItems: 'flex-end', flexWrap: 'wrap' }}>
            <div className="field">
              <label htmlFor="outcome">Resultado</label>
              <select id="outcome" value={outcome} onChange={(e) => setOutcome(e.target.value)}>
                <option value="APPROVED">Aprovar</option>
                <option value="PENDING">Gerar pendência</option>
                <option value="REJECTED">Recusar</option>
              </select>
            </div>
            <div className="field" style={{ flex: 1, minWidth: 260 }}>
              <label htmlFor="reason">Motivo (mínimo 5 caracteres)</label>
              <input
                id="reason"
                value={reason}
                placeholder="Ex.: risco dentro do apetite, sem sinistralidade previa"
                onChange={(e) => setReason(e.target.value)}
              />
            </div>
            <button className="btn" disabled={busy || reason.trim().length < 5} onClick={decide}>
              {busy ? 'Registrando…' : 'Registrar decisão'}
            </button>
          </div>
        )}
      </div>

      {data.pendencies.length > 0 && (
        <div className="panel">
          <header className="panel-head"><div><h2>Pendências</h2></div></header>
          <table>
            <thead><tr><th>Código</th><th>Descrição</th><th>Aberta em</th><th>Resolvida</th></tr></thead>
            <tbody>
              {data.pendencies.map((item) => (
                <tr key={item.id}>
                  <td className="mono">{item.code}</td>
                  <td>{item.description}</td>
                  <td>{dateTime(item.openedAt)}</td>
                  <td>{item.resolvedAt ? dateTime(item.resolvedAt) : <span className="badge warn">em aberto</span>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <div className="panel">
        <header className="panel-head">
          <div>
            <h2>Emissão da apólice</h2>
            <div className="sub">
              Uma única transação grava apólice, coberturas congeladas, plano de parcelamento,
              comissão e evento de domínio
            </div>
          </div>
        </header>

        <div style={{ padding: 16 }}>
          {p.policyId ? (
            <div className="result-box blocked">
              <strong>Apólice {p.policyNumber} já emitida.</strong> Uma nova tentativa é barrada
              em três camadas independentes: a chave de idempotência devolve a resposta original,
              o <code>xmin</code> da proposta não confere mais, e o índice único parcial{' '}
              <code>ux_policies_proposal</code> recusa a segunda linha no banco.
            </div>
          ) : (
            <>
              <p style={{ marginTop: 0, fontSize: 13.5, color: 'var(--pdc-slate-700)' }}>
                Chave de idempotência desta sessão:{' '}
                <code className="mono">{idempotencyKey.slice(0, 18)}…</code> — clicar duas vezes
                não emite duas apólices.
              </p>
              <button className="btn" disabled={!canIssue || busy} onClick={issue}>
                {busy ? 'Emitindo…' : 'Emitir apólice'}
              </button>
              {!canIssue && (
                <span className="hint-text" style={{ marginLeft: 12 }}>
                  {p.openPendencies > 0
                    ? `${p.openPendencies} pendência(s) em aberto bloqueiam a emissão`
                    : 'a proposta precisa estar aprovada na análise de risco'}
                </span>
              )}
            </>
          )}
        </div>
      </div>

      <div className="panel">
        <header className="panel-head">
          <div>
            <h2>Histórico de status</h2>
            <div className="sub">Trilha append-only gravada por gatilho na própria transação</div>
          </div>
        </header>
        <div className="timeline">
          {data.history.map((h, index) => (
            <div className="timeline-item" key={index}>
              <div className="timeline-dot" />
              <div>
                <div className="timeline-kind">
                  {h.fromStatus ? `${h.fromStatus} → ${h.toStatus}` : h.toStatus}
                </div>
                <div className="timeline-desc">{h.reason ?? '—'}</div>
                <div className="timeline-time">{dateTime(h.changedAt)}</div>
              </div>
            </div>
          ))}
        </div>
      </div>
    </>
  )
}

export function ProposalsPage({ tenantId }: { tenantId: string }) {
  const [brokers, setBrokers] = useState<Broker[]>([])
  const [actorId, setActorId] = useState('')
  const [data, setData] = useState<PagedResult<ProposalSummary> | null>(null)
  const [status, setStatus] = useState('')
  const [page, setPage] = useState(1)
  const [loading, setLoading] = useState(true)
  const [openId, setOpenId] = useState<string | null>(null)
  const { notify, view: toastView } = useToasts()

  useEffect(() => {
    api.brokers(tenantId).then((list) => {
      setBrokers(list)
      setActorId(list[0]?.userId ?? '')
    })
  }, [tenantId])

  const load = useCallback(async () => {
    setLoading(true)
    try {
      setData(await proposalApi.list(tenantId, { status: status || undefined, page }))
    } finally {
      setLoading(false)
    }
  }, [tenantId, status, page])

  useEffect(() => { void load() }, [load])

  if (openId) {
    return (
      <>
        {toastView}
        <ProposalDetailView
          tenantId={tenantId}
          actorId={actorId}
          id={openId}
          notify={notify}
          onClose={() => { setOpenId(null); void load() }}
        />
      </>
    )
  }

  return (
    <>
      {toastView}

      <div className="filters">
        <select
          className="search"
          value={actorId}
          onChange={(e) => setActorId(e.target.value)}
          aria-label="Corretor responsável"
        >
          {brokers.map((b) => (
            <option key={b.userId} value={b.userId}>{b.fullName}</option>
          ))}
        </select>

        <select className="search" value={status}
                onChange={(e) => { setStatus(e.target.value); setPage(1) }}>
          <option value="">Todos os status</option>
          <option value="SUBMITTED">Submetidas</option>
          <option value="UNDER_ANALYSIS">Em análise</option>
          <option value="APPROVED">Aprovadas</option>
          <option value="ISSUED">Emitidas</option>
          <option value="REJECTED">Recusadas</option>
        </select>

        {data && <span className="filter-count">{data.total} registro(s)</span>}
      </div>

      <div className="note">
        A emissão só é liberada para o <strong>corretor responsável pela proposta</strong>. Trocar
        o corretor no seletor acima e tentar emitir devolve <code>403 NOT_PROPOSAL_OWNER</code> —
        e mesmo que a checagem da aplicação fosse removida, a política <code>RESTRICTIVE</code> de
        comissões recusaria a gravação no banco.
      </div>

      <section className="panel">
        <header className="panel-head">
          <div>
            <h2>Propostas</h2>
            <div className="sub">Análise simulada de risco e emissão de apólice</div>
          </div>
        </header>

        {loading && <div className="state">Carregando…</div>}
        {!loading && data?.items.length === 0 && (
          <div className="state">Nenhuma proposta com este filtro.</div>
        )}

        {!loading && data && data.items.length > 0 && (
          <table>
            <thead>
              <tr>
                <th>Número</th><th>Cliente</th><th>Plano</th>
                <th className="num">Prêmio</th><th>Apólice</th><th>Status</th><th />
              </tr>
            </thead>
            <tbody>
              {data.items.map((p) => (
                <tr key={p.id}>
                  <td className="mono">{p.number}</td>
                  <td>{p.customerName}</td>
                  <td>
                    {PLAN_LABEL[p.chosenPlan] ?? p.chosenPlan}
                    <span className="hint-text"> · {p.installmentCount}×</span>
                  </td>
                  <td className="num">{money(p.totalPremium)}</td>
                  <td className="mono">{p.policyNumber ?? '—'}</td>
                  <td>
                    <span className={`badge ${
                      p.status === 'ISSUED' ? 'ok' :
                      p.status === 'REJECTED' ? 'danger' :
                      p.status === 'APPROVED' ? 'info' : 'warn'}`}>
                      {p.status}
                    </span>
                    {p.openPendencies > 0 && (
                      <span className="badge warn" style={{ marginLeft: 6 }}>
                        {p.openPendencies} pend.
                      </span>
                    )}
                  </td>
                  <td>
                    <button className="btn ghost small" onClick={() => setOpenId(p.id)}>
                      Abrir
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}

        {data && data.total > data.pageSize && (
          <div className="pagination">
            <button className="btn ghost" disabled={page === 1} onClick={() => setPage((p) => p - 1)}>
              Anterior
            </button>
            <span>página {page} de {Math.ceil(data.total / data.pageSize)}</span>
            <button
              className="btn ghost"
              disabled={page >= Math.ceil(data.total / data.pageSize)}
              onClick={() => setPage((p) => p + 1)}
            >
              Próxima
            </button>
          </div>
        )}
      </section>
    </>
  )
}
