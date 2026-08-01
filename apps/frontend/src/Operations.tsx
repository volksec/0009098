import { useCallback, useEffect, useState } from 'react'
import {
  ApiError, api, billingApi, claimApi, commissionApi,
  type BillingSummary, type Claim, type ClaimDetail,
  type Commission, type Installment, type MonthlyCommission,
  type PagedResult, type Policy,
} from './api'

const money = (value: number) =>
  new Intl.NumberFormat('pt-BR', { style: 'currency', currency: 'BRL' }).format(value)

const shortDate = (value: string) =>
  new Date(value).toLocaleDateString('pt-BR', { day: '2-digit', month: '2-digit', year: 'numeric' })

const monthLabel = (value: string) =>
  new Date(value).toLocaleDateString('pt-BR', { month: 'short', year: 'numeric' })

type Toast = { id: number; tone: 'ok' | 'error'; message: string }

function useToasts() {
  const [toasts, setToasts] = useState<Toast[]>([])

  const notify = useCallback((tone: Toast['tone'], message: string) => {
    const id = Date.now() + Math.random()
    setToasts((current) => [...current, { id, tone, message }])
    setTimeout(() => setToasts((current) => current.filter((t) => t.id !== id)), 4500)
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

// ================================================================ Faturamento

export function BillingPage() {
  const [summary, setSummary] = useState<BillingSummary | null>(null)
  const [data, setData] = useState<PagedResult<Installment> | null>(null)
  const [status, setStatus] = useState('')
  const [page, setPage] = useState(1)
  const [loading, setLoading] = useState(true)
  const [paying, setPaying] = useState<string | null>(null)
  const { notify, view: toastView } = useToasts()

  const load = useCallback(async () => {
    setLoading(true)
    try {
      const [s, list] = await Promise.all([
        billingApi.summary(),
        billingApi.installments({ status: status || undefined, page }),
      ])
      setSummary(s)
      setData(list)
    } finally {
      setLoading(false)
    }
  }, [status, page])

  useEffect(() => { void load() }, [load])
  useEffect(() => { setPage(1) }, [])

  const pay = async (installment: Installment) => {
    setPaying(installment.id)
    try {
      await billingApi.pay(installment.id, 'SIMULATED_PIX')
      notify('ok', `Parcela ${installment.sequence} quitada (pagamento simulado).`)
      await load()
    } catch (err) {
      notify('error', err instanceof Error ? err.message : 'Falha ao registrar pagamento')
    } finally {
      setPaying(null)
    }
  }

  return (
    <>
      {summary && (
        <div className="grid">
          <div className="card">
            <div className="label">Pendentes</div>
            <div className="value">{summary.pending}</div>
            <div className="hint">{money(summary.pendingAmount)}</div>
          </div>
          <div className="card">
            <div className="label">Vencidas</div>
            <div className="value" style={{ color: summary.overdue > 0 ? 'var(--pdc-red-600)' : undefined }}>
              {summary.overdue}
            </div>
            <div className="hint">{money(summary.overdueAmount)}</div>
          </div>
          <div className="card">
            <div className="label">Quitadas</div>
            <div className="value">{summary.paid}</div>
            <div className="hint">{money(summary.paidAmount)}</div>
          </div>
        </div>
      )}

      <section className="panel">
        <header className="panel-head">
          <div>
            <h2>Parcelas</h2>
            <div className="sub">
              A soma das parcelas é igual ao prêmio da apólice, ao centavo — garantido por
              constraint trigger deferida no banco
            </div>
          </div>
        </header>

        <div className="filters">
          <select
            className="search"
            value={status}
            onChange={(event) => { setStatus(event.target.value); setPage(1) }}
          >
            <option value="">Todos os status</option>
            <option value="PENDING">Pendentes</option>
            <option value="OVERDUE">Vencidas</option>
            <option value="PAID">Quitadas</option>
          </select>
        </div>

        {loading && <div className="state">Carregando…</div>}

        {data && data.items.length > 0 && (
          <>
            <table>
              <thead>
                <tr>
                  <th>Apólice</th><th>Cliente</th><th className="num">Parcela</th>
                  <th>Vencimento</th><th className="num">Valor</th><th>Status</th>
                  <th style={{ width: 110 }}>Ação</th>
                </tr>
              </thead>
              <tbody>
                {data.items.map((item) => (
                  <tr key={item.id}>
                    <td className="mono" style={{ fontSize: 12 }}>{item.policyNumber}</td>
                    <td>{item.customerName}</td>
                    <td className="num">{item.sequence}</td>
                    <td>{shortDate(item.dueDate)}</td>
                    <td className="num">{money(item.amount)}</td>
                    <td>
                      <span className={`badge ${
                        item.status === 'PAID' ? 'ok' :
                        item.isOverdue || item.status === 'OVERDUE' ? 'danger' : 'warn'}`}>
                        {item.status === 'PAID' ? 'QUITADA'
                          : item.isOverdue || item.status === 'OVERDUE' ? 'VENCIDA' : 'PENDENTE'}
                      </span>
                    </td>
                    <td>
                      {item.status !== 'PAID' && (
                        <button
                          className="btn ghost sm"
                          onClick={() => pay(item)}
                          disabled={paying === item.id}
                        >
                          {paying === item.id ? '…' : 'Quitar'}
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>

            <div className="pagination">
              <span>Página {data.pageNumber} de {data.totalPages} · {data.total} parcela(s)</span>
              <div style={{ display: 'flex', gap: 6 }}>
                <button className="btn ghost sm" disabled={!data.hasPrevious}
                        onClick={() => setPage((p) => p - 1)}>← Anterior</button>
                <button className="btn ghost sm" disabled={!data.hasNext}
                        onClick={() => setPage((p) => p + 1)}>Próxima →</button>
              </div>
            </div>
          </>
        )}
      </section>

      <div className="note">
        O <strong>Billing Scheduler</strong> roda de hora em hora e marca como vencidas as
        parcelas com data anterior a hoje. A coluna de status também deriva a condição por
        comparação de data, então a interface fica correta mesmo entre duas execuções do worker.
        Pagamentos são <strong>simulados</strong> — os métodos são prefixados com{' '}
        <code>SIMULATED_</code> no banco para que nenhuma tela possa apresentá-los como
        transação real.
      </div>

      {toastView}
    </>
  )
}

// ================================================================ Comissões

export function CommissionsPage() {
  const [data, setData] = useState<PagedResult<Commission> | null>(null)
  const [monthly, setMonthly] = useState<MonthlyCommission[]>([])
  const [page, setPage] = useState(1)
  const [loading, setLoading] = useState(false)
  const [reversing, setReversing] = useState<Commission | null>(null)
  const [reason, setReason] = useState('')
  const { notify, view: toastView } = useToasts()

  const load = useCallback(async () => {
    setLoading(true)
    try {
      const [list, month] = await Promise.all([
        commissionApi.list({ page }),
        commissionApi.monthly(),
      ])
      setData(list)
      setMonthly(month)
    } finally {
      setLoading(false)
    }
  }, [page])

  useEffect(() => { void load() }, [load])

  const release = async (commission: Commission) => {
    try {
      await commissionApi.release(commission.id)
      notify('ok', 'Comissão liberada.')
      await load()
    } catch (err) {
      notify('error', err instanceof Error ? err.message : 'Falha ao liberar')
    }
  }

  const reverse = async () => {
    if (!reversing) return
    try {
      await commissionApi.reverse(reversing.id, reason)
      notify('ok', 'Estorno lançado como movimento inverso.')
      setReversing(null)
      setReason('')
      await load()
    } catch (err) {
      notify('error', err instanceof Error ? err.message : 'Falha ao estornar')
    }
  }

  return (
    <>
      <section className="panel">
        <header className="panel-head">
          <div>
            <h2>Extrato de comissões</h2>
            <div className="sub">
              Filtrado por <code>broker_id = app.current_actor()</code>, que sai do token —
              a política RESTRICTIVE mostra apenas as comissões de quem está autenticado
            </div>
          </div>
        </header>

        {loading && <div className="state">Carregando…</div>}
        {data && data.items.length === 0 && !loading && (
          <div className="state">Nenhuma comissão para este corretor.</div>
        )}

        {data && data.items.length > 0 && (
          <>
            <table>
              <thead>
                <tr>
                  <th>Apólice</th><th>Cliente</th><th>Competência</th>
                  <th className="num">Base</th><th className="num">Taxa</th>
                  <th className="num">Valor</th><th>Status</th><th style={{ width: 150 }}>Ações</th>
                </tr>
              </thead>
              <tbody>
                {data.items.map((item) => (
                  <tr key={item.id}>
                    <td className="mono" style={{ fontSize: 12 }}>{item.policyNumber}</td>
                    <td>{item.customerName}</td>
                    <td>{monthLabel(item.referenceMonth)}</td>
                    <td className="num">{money(item.baseAmount)}</td>
                    <td className="num">{(item.rateApplied * 100).toFixed(2)}%</td>
                    <td className="num" style={{
                      color: item.amount < 0 ? 'var(--pdc-red-600)' : undefined,
                      fontWeight: 550,
                    }}>
                      {money(item.amount)}
                    </td>
                    <td>
                      <span className={`badge ${
                        item.status === 'REVERSED' ? 'danger' :
                        item.status === 'PAID' ? 'ok' :
                        item.status === 'RELEASED' ? 'info' : 'warn'}`}>
                        {item.status}
                      </span>
                    </td>
                    <td>
                      <div style={{ display: 'flex', gap: 6 }}>
                        {item.status === 'FORECAST' && (
                          <button className="btn ghost sm" onClick={() => release(item)}>
                            Liberar
                          </button>
                        )}
                        {item.status !== 'REVERSED' && (
                          <button className="btn danger sm" onClick={() => setReversing(item)}>
                            Estornar
                          </button>
                        )}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>

            <div className="pagination">
              <span>Página {data.pageNumber} de {data.totalPages} · {data.total} lançamento(s)</span>
              <div style={{ display: 'flex', gap: 6 }}>
                <button className="btn ghost sm" disabled={!data.hasPrevious}
                        onClick={() => setPage((p) => p - 1)}>← Anterior</button>
                <button className="btn ghost sm" disabled={!data.hasNext}
                        onClick={() => setPage((p) => p + 1)}>Próxima →</button>
              </div>
            </div>
          </>
        )}
      </section>

      {monthly.length > 0 && (
        <section className="panel">
          <header className="panel-head">
            <div><h2>Consolidação mensal</h2></div>
          </header>
          <table>
            <thead>
              <tr>
                <th>Competência</th><th className="num">Lançamentos</th>
                <th className="num">Prevista</th><th className="num">Liberada</th>
                <th className="num">Estornada</th><th className="num">Total</th>
              </tr>
            </thead>
            <tbody>
              {monthly.map((row) => (
                <tr key={row.referenceMonth}>
                  <td>{monthLabel(row.referenceMonth)}</td>
                  <td className="num">{row.count}</td>
                  <td className="num">{money(row.forecast)}</td>
                  <td className="num">{money(row.released)}</td>
                  <td className="num" style={{ color: row.reversed < 0 ? 'var(--pdc-red-600)' : undefined }}>
                    {money(row.reversed)}
                  </td>
                  <td className="num" style={{ fontWeight: 600 }}>{money(row.total)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
      )}

      <div className="note">
        Troque o corretor no seletor: o extrato muda porque a política <code>RESTRICTIVE</code>
        de <code>commissions</code> filtra por <code>broker_id = app.current_actor()</code>. É a
        segunda dimensão de autorização (ABAC) atuando sobre a primeira (tenant) — um corretor
        não vê a comissão do colega nem dentro da própria corretora.
        <br /><br />
        Cada lançamento guarda <code>rule_id</code>, <code>rule_version</code>,{' '}
        <code>rate_applied</code> e <code>base_amount</code>. Ainda que a regra mude amanhã, a
        pergunta "por que essa comissão foi esse valor?" continua respondível. O estorno cria um
        lançamento <strong>inverso</strong>, nunca sobrescreve o original — o histórico precisa
        mostrar que houve comissão e depois estorno, não que a comissão nunca existiu.
      </div>

      {reversing && (
        <div className="modal-backdrop" onClick={() => setReversing(null)}>
          <div className="modal small" onClick={(event) => event.stopPropagation()}>
            <header className="modal-head">
              <h3>Estornar comissão</h3>
              <button className="icon-btn" onClick={() => setReversing(null)}>×</button>
            </header>
            <div className="modal-body">
              <p style={{ marginTop: 0, fontSize: 13.5 }}>
                Será criado um lançamento de <strong>{money(-reversing.amount)}</strong> apontando
                para o original. O lançamento estornado permanece no extrato.
              </p>
              <label className="field">
                <span>Motivo (mínimo 5 caracteres)</span>
                <input value={reason} onChange={(e) => setReason(e.target.value)} autoFocus />
              </label>
            </div>
            <footer className="modal-foot">
              <button className="btn ghost" onClick={() => setReversing(null)}>Cancelar</button>
              <button className="btn danger" onClick={reverse} disabled={reason.trim().length < 5}>
                Confirmar estorno
              </button>
            </footer>
          </div>
        </div>
      )}

      {toastView}
    </>
  )
}

// ================================================================ Sinistros

export function ClaimsPage() {
  const [data, setData] = useState<PagedResult<Claim> | null>(null)
  const [detail, setDetail] = useState<ClaimDetail | null>(null)
  const [policies, setPolicies] = useState<Policy[]>([])
  const [status, setStatus] = useState('')
  const [page, setPage] = useState(1)
  const [loading, setLoading] = useState(true)
  const [reporting, setReporting] = useState(false)
  const [form, setForm] = useState({ policyId: '', occurrenceDate: '', description: '', estimatedAmount: '' })
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({})
  const [formError, setFormError] = useState<string | null>(null)
  const { notify, view: toastView } = useToasts()

  const load = useCallback(async () => {
    setLoading(true)
    try {
      setData(await claimApi.list({ status: status || undefined, page }))
    } finally {
      setLoading(false)
    }
  }, [status, page])

  useEffect(() => { void load() }, [load])
  useEffect(() => {
    setPage(1)
    api.policies().then(setPolicies).catch(() => setPolicies([]))
  }, [])

  const openDetail = async (claim: Claim) => {
    setDetail(await claimApi.detail(claim.id))
  }

  const submit = async (event: React.FormEvent) => {
    event.preventDefault()
    setFieldErrors({})
    setFormError(null)
    try {
      const created = await claimApi.report({
        policyId: form.policyId,
        occurrenceDate: form.occurrenceDate,
        description: form.description,
        estimatedAmount: form.estimatedAmount ? Number(form.estimatedAmount) : null,
      })
      notify('ok', `Sinistro ${created.number} registrado.`)
      setReporting(false)
      setForm({ policyId: '', occurrenceDate: '', description: '', estimatedAmount: '' })
      await load()
    } catch (err) {
      if (err instanceof ApiError) {
        setFieldErrors(err.fieldErrors ?? {})
        setFormError(err.message)
      } else {
        setFormError('Falha inesperada.')
      }
    }
  }

  const fieldError = (name: string) => fieldErrors[name]?.[0]
  const selectedPolicy = policies.find((p) => p.id === form.policyId)

  return (
    <>
      <section className="panel">
        <header className="panel-head">
          <div>
            <h2>Sinistros</h2>
            <div className="sub">{data ? `${data.total} registro(s)` : 'Carregando…'}</div>
          </div>
          <button
            className="btn"
            onClick={() => { setReporting(true); setForm({ ...form, policyId: policies[0]?.id ?? '' }) }}
            disabled={policies.length === 0}
          >
            + Avisar sinistro
          </button>
        </header>

        <div className="filters">
          <select className="search" value={status}
                  onChange={(e) => { setStatus(e.target.value); setPage(1) }}>
            <option value="">Todos os status</option>
            <option value="REPORTED">Avisado</option>
            <option value="UNDER_ANALYSIS">Em análise</option>
            <option value="APPROVED">Aprovado</option>
            <option value="DENIED">Negado</option>
          </select>
        </div>

        {loading && <div className="state">Carregando…</div>}
        {data && data.items.length === 0 && !loading && (
          <div className="state">Nenhum sinistro com os filtros atuais.</div>
        )}

        {data && data.items.length > 0 && (
          <>
            <table>
              <thead>
                <tr>
                  <th>Número</th><th>Cliente</th><th>Apólice</th>
                  <th>Ocorrência</th><th className="num">Estimado</th>
                  <th className="num">Eventos</th><th>Status</th><th style={{ width: 90 }}></th>
                </tr>
              </thead>
              <tbody>
                {data.items.map((claim) => (
                  <tr key={claim.id}>
                    <td className="mono" style={{ fontSize: 12 }}>{claim.number}</td>
                    <td>{claim.customerName}</td>
                    <td className="mono" style={{ fontSize: 12 }}>{claim.policyNumber}</td>
                    <td>{shortDate(claim.occurrenceDate)}</td>
                    <td className="num">
                      {claim.estimatedAmount !== null ? money(claim.estimatedAmount) : '—'}
                    </td>
                    <td className="num">{claim.eventCount}</td>
                    <td>
                      <span className={`badge ${
                        claim.status === 'APPROVED' || claim.status === 'SETTLED' ? 'ok' :
                        claim.status === 'DENIED' ? 'danger' :
                        claim.status === 'UNDER_ANALYSIS' ? 'warn' : 'info'}`}>
                        {claim.status}
                      </span>
                    </td>
                    <td>
                      <button className="btn ghost sm" onClick={() => openDetail(claim)}>
                        Detalhe
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>

            <div className="pagination">
              <span>Página {data.pageNumber} de {data.totalPages} · {data.total} sinistro(s)</span>
              <div style={{ display: 'flex', gap: 6 }}>
                <button className="btn ghost sm" disabled={!data.hasPrevious}
                        onClick={() => setPage((p) => p - 1)}>← Anterior</button>
                <button className="btn ghost sm" disabled={!data.hasNext}
                        onClick={() => setPage((p) => p + 1)}>Próxima →</button>
              </div>
            </div>
          </>
        )}
      </section>

      <div className="note">
        A data do evento precisa estar <strong>dentro da vigência da apólice</strong>. A validação
        existe na API para dar uma mensagem melhor, mas a garantia final é a trigger
        <code>tg_claims_within_coverage</code> no banco — nem um script manual consegue registrar
        sinistro fora da cobertura. A linha do tempo é <strong>append-only</strong>: eventos são
        acrescentados, nunca editados. Decisões e valores são <strong>simulados</strong>.
      </div>

      {/* ---------------------------------------------------------------- aviso */}
      {reporting && (
        <div className="modal-backdrop" onClick={() => setReporting(false)}>
          <div className="modal" onClick={(e) => e.stopPropagation()}>
            <header className="modal-head">
              <h3>Avisar sinistro</h3>
              <button className="icon-btn" onClick={() => setReporting(false)}>×</button>
            </header>
            <form onSubmit={submit} className="modal-body">
              {formError && <div className="alert error">{formError}</div>}

              <label className="field">
                <span>Apólice</span>
                <select value={form.policyId}
                        onChange={(e) => setForm({ ...form, policyId: e.target.value })}>
                  {policies.map((policy) => (
                    <option key={policy.id} value={policy.id}>
                      {policy.number} — {policy.customerName}
                    </option>
                  ))}
                </select>
                {selectedPolicy && (
                  <em className="hint-text">
                    Vigência: {shortDate(selectedPolicy.periodStart)} a{' '}
                    {shortDate(selectedPolicy.periodEnd)}
                  </em>
                )}
              </label>

              <div className="field-row">
                <label className="field">
                  <span>Data do evento</span>
                  <input type="date" value={form.occurrenceDate}
                         onChange={(e) => setForm({ ...form, occurrenceDate: e.target.value })}
                         className={fieldError('OccurrenceDate') ? 'invalid' : undefined} />
                  {fieldError('OccurrenceDate') && (
                    <em className="error-text">{fieldError('OccurrenceDate')}</em>
                  )}
                </label>
                <label className="field">
                  <span>Valor estimado (opcional)</span>
                  <input type="number" step="0.01" min="0" value={form.estimatedAmount}
                         onChange={(e) => setForm({ ...form, estimatedAmount: e.target.value })} />
                </label>
              </div>

              <label className="field">
                <span>Descrição do evento</span>
                <input value={form.description}
                       onChange={(e) => setForm({ ...form, description: e.target.value })}
                       placeholder="Mínimo 10 caracteres"
                       className={fieldError('Description') ? 'invalid' : undefined} />
                {fieldError('Description') && (
                  <em className="error-text">{fieldError('Description')}</em>
                )}
              </label>

              <footer className="modal-foot">
                <button type="button" className="btn ghost" onClick={() => setReporting(false)}>
                  Cancelar
                </button>
                <button className="btn" type="submit">Registrar aviso</button>
              </footer>
            </form>
          </div>
        </div>
      )}

      {/* ---------------------------------------------------------------- detalhe */}
      {detail && (
        <div className="modal-backdrop" onClick={() => setDetail(null)}>
          <div className="modal" onClick={(e) => e.stopPropagation()}>
            <header className="modal-head">
              <h3>{detail.claim.number}</h3>
              <button className="icon-btn" onClick={() => setDetail(null)}>×</button>
            </header>
            <div className="modal-body">
              <dl className="detail-list">
                {([
                  ['Apólice', detail.claim.policyNumber],
                  ['Vigência', `${shortDate(detail.claim.coverageStart)} a ${shortDate(detail.claim.coverageEnd)}`],
                  ['Ocorrência', shortDate(detail.claim.occurrenceDate)],
                  ['Status', detail.claim.status],
                  ['Estimado', detail.claim.estimatedAmount !== null ? money(detail.claim.estimatedAmount) : '—'],
                  ['Indenização', detail.claim.settledAmount !== null ? `${money(detail.claim.settledAmount)} (simulada)` : '—'],
                  ['Descrição', detail.claim.description],
                ] as [string, string][]).map(([label, value]) => (
                  <div key={label}>
                    <dt>{label}</dt>
                    <dd>{value}</dd>
                  </div>
                ))}
              </dl>

              <div className="detail-label">Linha do tempo (append-only)</div>
              <div className="timeline">
                {detail.timeline.map((event) => (
                  <div className="timeline-item" key={event.sequence}>
                    <div className="timeline-dot" />
                    <div>
                      <div className="timeline-kind">{event.kind}</div>
                      <div className="timeline-desc">{event.description}</div>
                      <div className="timeline-time">
                        {new Date(event.occurredAt).toLocaleString('pt-BR')}
                      </div>
                    </div>
                  </div>
                ))}
                {detail.timeline.length === 0 && (
                  <div className="state">Nenhum evento registrado.</div>
                )}
              </div>
            </div>
          </div>
        </div>
      )}

      {toastView}
    </>
  )
}
