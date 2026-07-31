import { useEffect, useMemo, useRef, useState } from 'react'
import { api, connectEventStream, type ProcessingEvent } from './api'

const MAX_EVENTS = 300

const CATEGORY_TONE: Record<string, string> = {
  DomainEvent: 'info',
  DatabaseQuery: 'muted',
  Transaction: 'info',
  AuthorizationDecision: 'warn',
  RowLevelSecurity: 'warn',
  AuditEvent: 'ok',
  SecurityEvent: 'danger',
  Error: 'danger',
}

const STATUS_TONE: Record<string, string> = {
  SUCCESS: 'ok',
  DENIED: 'warn',
  ERROR: 'danger',
}

export function LiveConsole() {
  const [events, setEvents] = useState<ProcessingEvent[]>([])
  const [state, setState] = useState<'connecting' | 'open' | 'closed'>('connecting')
  const [categoryFilter, setCategoryFilter] = useState('')
  const [textFilter, setTextFilter] = useState('')
  const [paused, setPaused] = useState(false)
  const [selected, setSelected] = useState<ProcessingEvent | null>(null)

  const pausedRef = useRef(paused)
  pausedRef.current = paused

  useEffect(() => {
    // Histórico recente primeiro, para que o console não abra vazio
    api.recentEvents().then((recent) => setEvents(recent.slice().reverse())).catch(() => {})

    const disconnect = connectEventStream(
      (event) => {
        if (pausedRef.current) return
        // Buffer limitado: um console aberto por horas não pode crescer sem limite
        setEvents((current) => [event, ...current].slice(0, MAX_EVENTS))
      },
      setState,
    )

    return disconnect
  }, [])

  const categories = useMemo(
    () => [...new Set(events.map((e) => e.category))].sort(),
    [events])

  const visible = useMemo(() => events.filter((event) => {
    if (categoryFilter && event.category !== categoryFilter) return false
    if (!textFilter) return true

    const needle = textFilter.toLowerCase()
    return event.message.toLowerCase().includes(needle)
        || event.operation.toLowerCase().includes(needle)
        || (event.correlationId ?? '').toLowerCase().includes(needle)
  }), [events, categoryFilter, textFilter])

  return (
    <>
      <section className="panel">
        <header className="panel-head">
          <div>
            <h2>Live Processing Console</h2>
            <div className="sub">
              Eventos internos em tempo real via Server-Sent Events ·{' '}
              <span className={`badge ${state === 'open' ? 'ok' : state === 'connecting' ? 'warn' : 'danger'}`}>
                {state === 'open' ? 'CONECTADO' : state === 'connecting' ? 'CONECTANDO' : 'DESCONECTADO'}
              </span>
            </div>
          </div>
          <div style={{ display: 'flex', gap: 8 }}>
            <button className="btn ghost sm" onClick={() => setPaused((p) => !p)}>
              {paused ? '▶ Retomar' : '⏸ Pausar'}
            </button>
            <button className="btn ghost sm" onClick={() => setEvents([])}>Limpar</button>
          </div>
        </header>

        <div className="filters">
          <input
            className="search"
            placeholder="Filtrar por mensagem, operação ou correlation ID…"
            value={textFilter}
            onChange={(event) => setTextFilter(event.target.value)}
          />
          <select
            className="search"
            value={categoryFilter}
            onChange={(event) => setCategoryFilter(event.target.value)}
          >
            <option value="">Todas as categorias</option>
            {categories.map((category) => (
              <option key={category} value={category}>{category}</option>
            ))}
          </select>
          <span className="filter-count">{visible.length} evento(s)</span>
        </div>

        {visible.length === 0 ? (
          <div className="state">
            Nenhum evento ainda. Vá até <strong>Administração</strong> e cadastre, edite ou exclua um
            cliente — as operações aparecem aqui em tempo real.
          </div>
        ) : (
          <div className="console">
            {visible.map((event) => (
              <button
                key={event.id}
                className="console-line"
                onClick={() => setSelected(event)}
                aria-label={`Detalhes de ${event.operation}`}
              >
                <span className="ts mono">
                  {new Date(event.timestamp).toLocaleTimeString('pt-BR', { hour12: false })}
                </span>
                <span className={`badge ${CATEGORY_TONE[event.category] ?? 'muted'}`}>
                  {event.category}
                </span>
                <span className="op mono">{event.operation}</span>
                <span className="msg">{event.message}</span>
                {event.durationMs !== null && (
                  <span className="dur mono">{event.durationMs} ms</span>
                )}
                <span className={`badge ${STATUS_TONE[event.status] ?? 'muted'}`}>
                  {event.status}
                </span>
              </button>
            ))}
          </div>
        )}
      </section>

      <div className="note">
        Cada linha é emitida pela própria API no caminho da operação, com redação aplicada antes da
        publicação — documento, e-mail e telefone nunca chegam ao console em claro. A publicação é
        não bloqueante: se um cliente do stream ficar lento, os eventos dele são descartados em vez
        de retardar a operação de negócio.
      </div>

      {selected && (
        <div className="modal-backdrop" onClick={() => setSelected(null)}>
          <div className="modal" onClick={(event) => event.stopPropagation()}>
            <header className="modal-head">
              <h3>{selected.operation}</h3>
              <button className="icon-btn" onClick={() => setSelected(null)}>×</button>
            </header>
            <div className="modal-body">
              <dl className="detail-list">
                {([
                  ['Categoria', selected.category],
                  ['Módulo', selected.module],
                  ['Status', selected.status],
                  ['Mensagem', selected.message],
                  ['Entidade', selected.entity ?? '—'],
                  ['ID da entidade', selected.entityId ?? '—'],
                  ['Tenant', selected.tenantId ?? '—'],
                  ['Correlation ID', selected.correlationId ?? '—'],
                  ['Duração', selected.durationMs !== null ? `${selected.durationMs} ms` : '—'],
                  ['Timestamp', new Date(selected.timestamp).toLocaleString('pt-BR')],
                ] as [string, string][]).map(([label, value]) => (
                  <div key={label}>
                    <dt>{label}</dt>
                    <dd className="mono">{value}</dd>
                  </div>
                ))}
              </dl>

              {selected.sql && (
                <>
                  <div className="detail-label">SQL executado</div>
                  <pre className="sql-block">{selected.sql}</pre>
                </>
              )}
            </div>
          </div>
        </div>
      )}
    </>
  )
}
