import { useCallback, useEffect, useState } from 'react'
import {
  ApiError, api,
  type Broker, type Customer, type CustomerInput, type PagedResult,
} from './api'

type Toast = { id: number; tone: 'ok' | 'error'; message: string }

const EMPTY_FORM: CustomerInput = {
  kind: 'INDIVIDUAL',
  brokerId: '',
  document: '',
  firstName: '', lastName: '', birthDate: '', occupation: '',
  legalName: '', tradeName: '', cnaeCode: '', companySize: 'MEDIUM',
  email: '', phone: '',
}

/** Máscara de CPF/CNPJ conforme o usuário digita. */
function maskDocument(value: string): string {
  const digits = value.replace(/\D/g, '').slice(0, 14)
  if (digits.length <= 11) {
    return digits
      .replace(/(\d{3})(\d)/, '$1.$2')
      .replace(/(\d{3})(\d)/, '$1.$2')
      .replace(/(\d{3})(\d{1,2})$/, '$1-$2')
  }
  return digits
    .replace(/(\d{2})(\d)/, '$1.$2')
    .replace(/(\d{3})(\d)/, '$1.$2')
    .replace(/(\d{3})(\d)/, '$1/$2')
    .replace(/(\d{4})(\d{1,2})$/, '$1-$2')
}

function maskPhone(value: string): string {
  const digits = value.replace(/\D/g, '').slice(0, 11)
  if (digits.length <= 10) {
    return digits.replace(/(\d{2})(\d)/, '($1) $2').replace(/(\d{4})(\d{1,4})$/, '$1-$2')
  }
  return digits.replace(/(\d{2})(\d)/, '($1) $2').replace(/(\d{5})(\d{1,4})$/, '$1-$2')
}

export function CustomerAdmin({ tenantId }: { tenantId: string }) {
  const [data, setData] = useState<PagedResult<Customer> | null>(null)
  const [brokers, setBrokers] = useState<Broker[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const [search, setSearch] = useState('')
  const [term, setTerm] = useState('')
  const [kind, setKind] = useState('')
  const [includeDeleted, setIncludeDeleted] = useState(false)
  const [page, setPage] = useState(1)
  const pageSize = 10

  const [form, setForm] = useState<CustomerInput | null>(null)
  const [editingId, setEditingId] = useState<string | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({})
  const [formError, setFormError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)

  const [deleting, setDeleting] = useState<Customer | null>(null)
  const [deleteReason, setDeleteReason] = useState('')

  const [toasts, setToasts] = useState<Toast[]>([])

  const notify = useCallback((tone: Toast['tone'], message: string) => {
    const id = Date.now() + Math.random()
    setToasts((current) => [...current, { id, tone, message }])
    setTimeout(() => setToasts((current) => current.filter((t) => t.id !== id)), 4500)
  }, [])

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const result = await api.customers(tenantId, {
        search: search || undefined,
        kind: kind || undefined,
        includeDeleted,
        page,
        pageSize,
      })
      setData(result)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Falha ao carregar')
    } finally {
      setLoading(false)
    }
  }, [tenantId, search, kind, includeDeleted, page])

  useEffect(() => { void load() }, [load])

  useEffect(() => {
    api.brokers(tenantId).then(setBrokers).catch(() => setBrokers([]))
    setPage(1)
  }, [tenantId])

  // ---------------------------------------------------------------- formulário

  const openCreate = () => {
    const first = brokers[0]?.id ?? ''
    setForm({ ...EMPTY_FORM, brokerId: first })
    setEditingId(null)
    setFieldErrors({})
    setFormError(null)
  }

  const openEdit = (customer: Customer) => {
    setForm({
      kind: customer.kind,
      brokerId: customer.brokerId,
      document: '',   // não editável: mudar o documento alteraria a identidade do cliente
      firstName: customer.firstName ?? '',
      lastName: customer.lastName ?? '',
      birthDate: customer.birthDate ? customer.birthDate.slice(0, 10) : '',
      occupation: customer.occupation ?? '',
      legalName: customer.legalName ?? '',
      tradeName: customer.tradeName ?? '',
      cnaeCode: customer.cnaeCode ?? '',
      companySize: customer.companySize ?? 'MEDIUM',
      email: customer.email ?? '',
      phone: customer.phone ?? '',
    })
    setEditingId(customer.id)
    setFieldErrors({})
    setFormError(null)
  }

  const submit = async (event: React.FormEvent) => {
    event.preventDefault()
    if (!form) return

    setSaving(true)
    setFieldErrors({})
    setFormError(null)

    const payload: CustomerInput = {
      ...form,
      document: form.document.replace(/\D/g, ''),
      phone: form.phone?.replace(/\D/g, '') || null,
      birthDate: form.birthDate || null,
      // Campos do outro tipo vão nulos: o CHECK do banco recusa a mistura
      ...(form.kind === 'INDIVIDUAL'
        ? { legalName: null, tradeName: null, cnaeCode: null, companySize: null }
        : { firstName: null, lastName: null, birthDate: null, occupation: null }),
    }

    try {
      if (editingId) {
        // Documento e tipo não seguem na edição: alterá-los mudaria a identidade do
        // cliente e invalidaria o histórico de apólices emitidas em seu nome.
        const { document: _document, kind: _kind, ...updatable } = payload
        await api.updateCustomer(tenantId, editingId, updatable)
        notify('ok', 'Cliente atualizado.')
      } else {
        await api.createCustomer(tenantId, payload)
        notify('ok', 'Cliente cadastrado.')
      }
      setForm(null)
      setEditingId(null)
      await load()
    } catch (err) {
      if (err instanceof ApiError) {
        setFieldErrors(err.fieldErrors ?? {})
        setFormError(err.message)
      } else {
        setFormError('Falha inesperada.')
      }
    } finally {
      setSaving(false)
    }
  }

  const confirmDelete = async () => {
    if (!deleting) return
    try {
      await api.deleteCustomer(tenantId, deleting.id, deleteReason)
      notify('ok', 'Cliente excluído logicamente.')
      setDeleting(null)
      setDeleteReason('')
      await load()
    } catch (err) {
      notify('error', err instanceof Error ? err.message : 'Falha ao excluir')
    }
  }

  const restore = async (customer: Customer) => {
    try {
      await api.restoreCustomer(tenantId, customer.id)
      notify('ok', 'Cliente restaurado.')
      await load()
    } catch (err) {
      notify('error', err instanceof Error ? err.message : 'Falha ao restaurar')
    }
  }

  const fieldError = (name: string) => fieldErrors[name]?.[0]

  return (
    <>
      <section className="panel">
        <header className="panel-head">
          <div>
            <h2>Administração de clientes</h2>
            <div className="sub">
              {data ? `${data.total} registro(s)` : 'Carregando…'} · cadastro, edição e exclusão
              lógica persistidos no PostgreSQL
            </div>
          </div>
          <button className="btn" onClick={openCreate} disabled={brokers.length === 0}>
            + Novo cliente
          </button>
        </header>

        <div className="filters">
          <form
            onSubmit={(event) => { event.preventDefault(); setSearch(term); setPage(1) }}
            style={{ display: 'contents' }}
          >
            <input
              className="search"
              placeholder="Buscar por nome…"
              value={term}
              onChange={(event) => setTerm(event.target.value)}
            />
            <select
              className="search"
              value={kind}
              onChange={(event) => { setKind(event.target.value); setPage(1) }}
            >
              <option value="">Todos os tipos</option>
              <option value="INDIVIDUAL">Pessoa física</option>
              <option value="BUSINESS">Pessoa jurídica</option>
            </select>
            <label className="check">
              <input
                type="checkbox"
                checked={includeDeleted}
                onChange={(event) => { setIncludeDeleted(event.target.checked); setPage(1) }}
              />
              Incluir excluídos
            </label>
            <button className="btn ghost" type="submit">Filtrar</button>
          </form>
        </div>

        {loading && <div className="state">Carregando…</div>}
        {error && <div className="state">Falha: {error}</div>}
        {data && data.items.length === 0 && !loading && (
          <div className="state">Nenhum cliente encontrado com os filtros atuais.</div>
        )}

        {data && data.items.length > 0 && (
          <>
            <table>
              <thead>
                <tr>
                  <th>Nome</th><th>Tipo</th><th>Corretor</th><th>Contato</th>
                  <th className="num">Bens</th><th className="num">Apólices</th>
                  <th>Status</th><th style={{ width: 168 }}>Ações</th>
                </tr>
              </thead>
              <tbody>
                {data.items.map((customer) => (
                  <tr key={customer.id} className={customer.deletedAt ? 'deleted-row' : undefined}>
                    <td>
                      {customer.displayName}
                      {customer.deletedAt && (
                        <div className="row-note">excluído: {customer.deletionReason}</div>
                      )}
                    </td>
                    <td>
                      <span className={`badge ${customer.kind === 'BUSINESS' ? 'info' : 'muted'}`}>
                        {customer.kind === 'BUSINESS' ? 'PJ' : 'PF'}
                      </span>
                    </td>
                    <td>{customer.brokerName}</td>
                    <td className="mono" style={{ fontSize: 12 }}>
                      {customer.email ?? customer.phone ?? '—'}
                    </td>
                    <td className="num">{customer.assetCount}</td>
                    <td className="num">{customer.activePolicies}</td>
                    <td>
                      <span className={`badge ${customer.deletedAt ? 'danger' : 'ok'}`}>
                        {customer.deletedAt ? 'EXCLUÍDO' : customer.status}
                      </span>
                    </td>
                    <td>
                      {customer.deletedAt ? (
                        <button className="btn ghost sm" onClick={() => restore(customer)}>
                          Restaurar
                        </button>
                      ) : (
                        <div style={{ display: 'flex', gap: 6 }}>
                          <button className="btn ghost sm" onClick={() => openEdit(customer)}>
                            Editar
                          </button>
                          <button className="btn danger sm" onClick={() => setDeleting(customer)}>
                            Excluir
                          </button>
                        </div>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>

            <div className="pagination">
              <span>
                Página {data.pageNumber} de {data.totalPages} · {data.total} registro(s)
              </span>
              <div style={{ display: 'flex', gap: 6 }}>
                <button
                  className="btn ghost sm"
                  disabled={!data.hasPrevious}
                  onClick={() => setPage((p) => p - 1)}
                >
                  ← Anterior
                </button>
                <button
                  className="btn ghost sm"
                  disabled={!data.hasNext}
                  onClick={() => setPage((p) => p + 1)}
                >
                  Próxima →
                </button>
              </div>
            </div>
          </>
        )}
      </section>

      <div className="note">
        A exclusão é <strong>lógica</strong>: o registro recebe <code>deleted_at</code>,
        <code>deleted_by</code> e um motivo obrigatório, e a cascata marca contatos, endereços e
        bens na mesma transação. O privilégio de <code>DELETE</code> físico é revogado do papel da
        aplicação no banco, então nem um bug consegue destruir histórico. Marque
        "incluir excluídos" para ver e restaurar.
      </div>

      {/* ---------------------------------------------------------------- formulário */}
      {form && (
        <div className="modal-backdrop" onClick={() => !saving && setForm(null)}>
          <div className="modal" onClick={(event) => event.stopPropagation()}>
            <header className="modal-head">
              <h3>{editingId ? 'Editar cliente' : 'Novo cliente'}</h3>
              <button className="icon-btn" onClick={() => setForm(null)} disabled={saving}>×</button>
            </header>

            <form onSubmit={submit} className="modal-body">
              {formError && <div className="alert error">{formError}</div>}

              <div className="field-row">
                <label className="field">
                  <span>Tipo de pessoa</span>
                  <select
                    value={form.kind}
                    disabled={!!editingId}
                    onChange={(event) =>
                      setForm({ ...form, kind: event.target.value as 'INDIVIDUAL' | 'BUSINESS' })}
                  >
                    <option value="INDIVIDUAL">Pessoa física</option>
                    <option value="BUSINESS">Pessoa jurídica</option>
                  </select>
                  {editingId && <em className="hint-text">O tipo não é editável.</em>}
                </label>

                <label className="field">
                  <span>Corretor responsável</span>
                  <select
                    value={form.brokerId}
                    onChange={(event) => setForm({ ...form, brokerId: event.target.value })}
                  >
                    {brokers.map((broker) => (
                      <option key={broker.id} value={broker.id}>{broker.fullName}</option>
                    ))}
                  </select>
                  {fieldError('BrokerId') && <em className="error-text">{fieldError('BrokerId')}</em>}
                </label>
              </div>

              {!editingId && (
                <label className="field">
                  <span>{form.kind === 'INDIVIDUAL' ? 'CPF' : 'CNPJ'}</span>
                  <input
                    value={maskDocument(form.document)}
                    onChange={(event) => setForm({ ...form, document: event.target.value })}
                    placeholder={form.kind === 'INDIVIDUAL' ? '000.000.000-00' : '00.000.000/0000-00'}
                    className={fieldError('Document') ? 'invalid' : undefined}
                  />
                  {fieldError('Document') && <em className="error-text">{fieldError('Document')}</em>}
                  <em className="hint-text">
                    Validado por dígito verificador no Value Object <code>DocumentNumber</code>.
                  </em>
                </label>
              )}

              {form.kind === 'INDIVIDUAL' ? (
                <>
                  <div className="field-row">
                    <label className="field">
                      <span>Nome</span>
                      <input
                        value={form.firstName ?? ''}
                        onChange={(event) => setForm({ ...form, firstName: event.target.value })}
                        className={fieldError('FirstName') ? 'invalid' : undefined}
                      />
                      {fieldError('FirstName') && <em className="error-text">{fieldError('FirstName')}</em>}
                    </label>
                    <label className="field">
                      <span>Sobrenome</span>
                      <input
                        value={form.lastName ?? ''}
                        onChange={(event) => setForm({ ...form, lastName: event.target.value })}
                        className={fieldError('LastName') ? 'invalid' : undefined}
                      />
                      {fieldError('LastName') && <em className="error-text">{fieldError('LastName')}</em>}
                    </label>
                  </div>
                  <div className="field-row">
                    <label className="field">
                      <span>Data de nascimento</span>
                      <input
                        type="date"
                        value={form.birthDate ?? ''}
                        onChange={(event) => setForm({ ...form, birthDate: event.target.value })}
                      />
                    </label>
                    <label className="field">
                      <span>Profissão</span>
                      <input
                        value={form.occupation ?? ''}
                        onChange={(event) => setForm({ ...form, occupation: event.target.value })}
                      />
                    </label>
                  </div>
                </>
              ) : (
                <>
                  <div className="field-row">
                    <label className="field">
                      <span>Razão social</span>
                      <input
                        value={form.legalName ?? ''}
                        onChange={(event) => setForm({ ...form, legalName: event.target.value })}
                        className={fieldError('LegalName') ? 'invalid' : undefined}
                      />
                      {fieldError('LegalName') && <em className="error-text">{fieldError('LegalName')}</em>}
                    </label>
                    <label className="field">
                      <span>Nome fantasia</span>
                      <input
                        value={form.tradeName ?? ''}
                        onChange={(event) => setForm({ ...form, tradeName: event.target.value })}
                      />
                    </label>
                  </div>
                  <div className="field-row">
                    <label className="field">
                      <span>CNAE</span>
                      <input
                        value={form.cnaeCode ?? ''}
                        onChange={(event) => setForm({ ...form, cnaeCode: event.target.value })}
                        placeholder="4711-3"
                        className={fieldError('CnaeCode') ? 'invalid' : undefined}
                      />
                      {fieldError('CnaeCode') && <em className="error-text">{fieldError('CnaeCode')}</em>}
                    </label>
                    <label className="field">
                      <span>Porte</span>
                      <select
                        value={form.companySize ?? 'MEDIUM'}
                        onChange={(event) => setForm({ ...form, companySize: event.target.value })}
                      >
                        <option value="MICRO">Micro</option>
                        <option value="SMALL">Pequeno</option>
                        <option value="MEDIUM">Médio</option>
                        <option value="LARGE">Grande</option>
                      </select>
                    </label>
                  </div>
                </>
              )}

              <div className="field-row">
                <label className="field">
                  <span>E-mail</span>
                  <input
                    type="email"
                    value={form.email ?? ''}
                    onChange={(event) => setForm({ ...form, email: event.target.value })}
                    className={fieldError('Email') ? 'invalid' : undefined}
                  />
                  {fieldError('Email') && <em className="error-text">{fieldError('Email')}</em>}
                </label>
                <label className="field">
                  <span>Telefone</span>
                  <input
                    value={maskPhone(form.phone ?? '')}
                    onChange={(event) => setForm({ ...form, phone: event.target.value })}
                    placeholder="(11) 98765-4321"
                  />
                </label>
              </div>

              <footer className="modal-foot">
                <button
                  type="button"
                  className="btn ghost"
                  onClick={() => setForm(null)}
                  disabled={saving}
                >
                  Cancelar
                </button>
                <button className="btn" type="submit" disabled={saving}>
                  {saving ? 'Salvando…' : editingId ? 'Salvar alterações' : 'Cadastrar'}
                </button>
              </footer>
            </form>
          </div>
        </div>
      )}

      {/* ---------------------------------------------------------------- exclusão */}
      {deleting && (
        <div className="modal-backdrop" onClick={() => setDeleting(null)}>
          <div className="modal small" onClick={(event) => event.stopPropagation()}>
            <header className="modal-head">
              <h3>Excluir cliente</h3>
              <button className="icon-btn" onClick={() => setDeleting(null)}>×</button>
            </header>
            <div className="modal-body">
              <p style={{ marginTop: 0, fontSize: 13.5 }}>
                Excluindo <strong>{deleting.displayName}</strong>. A exclusão é lógica e pode ser
                revertida. Clientes com apólice vigente são recusados pelo servidor.
              </p>
              <label className="field">
                <span>Motivo (obrigatório, mínimo 5 caracteres)</span>
                <input
                  value={deleteReason}
                  onChange={(event) => setDeleteReason(event.target.value)}
                  placeholder="Ex.: solicitação do titular"
                  autoFocus
                />
              </label>
            </div>
            <footer className="modal-foot">
              <button className="btn ghost" onClick={() => setDeleting(null)}>Cancelar</button>
              <button
                className="btn danger"
                onClick={confirmDelete}
                disabled={deleteReason.trim().length < 5}
              >
                Confirmar exclusão
              </button>
            </footer>
          </div>
        </div>
      )}

      <div className="toasts">
        {toasts.map((toast) => (
          <div key={toast.id} className={`toast ${toast.tone}`}>{toast.message}</div>
        ))}
      </div>
    </>
  )
}
