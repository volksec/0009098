const BASE_URL = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:8080'

export interface Brokerage {
  id: string
  tradeName: string
  susepRegistration: string
  status: string
}

export interface Broker {
  id: string
  fullName: string
  susepRegistration: string
  status: string
}

export interface DashboardSummary {
  customers: number
  openQuotations: number
  pendingProposals: number
  activePolicies: number
  openClaims: number
  forecastCommission: number
  upcomingRenewals: number
}

export interface Customer {
  id: string
  kind: 'INDIVIDUAL' | 'BUSINESS'
  status: string
  displayName: string
  firstName: string | null
  lastName: string | null
  birthDate: string | null
  occupation: string | null
  legalName: string | null
  tradeName: string | null
  cnaeCode: string | null
  companySize: string | null
  brokerId: string
  brokerName: string
  createdAt: string
  deletedAt: string | null
  deletionReason: string | null
  assetCount: number
  activePolicies: number
  email: string | null
  phone: string | null
}

export interface CustomerInput {
  kind: 'INDIVIDUAL' | 'BUSINESS'
  brokerId: string
  document: string
  firstName?: string | null
  lastName?: string | null
  birthDate?: string | null
  occupation?: string | null
  legalName?: string | null
  tradeName?: string | null
  cnaeCode?: string | null
  companySize?: string | null
  email?: string | null
  phone?: string | null
}

export interface Policy {
  id: string
  number: string
  status: string
  periodStart: string
  periodEnd: string
  totalPremium: number
  productName: string
  customerName: string
  issuedAt: string
}

export interface SchemaStats {
  tables: number
  indexes: number
  tablesWithRls: number
  rlsPolicies: number
  partitions: number
  exclusionConstraints: number
  enums: number
  compositeTypes: number
}

export interface RlsPolicy {
  table: string
  policy: string
  command: string
  roles: string
  forced: boolean
}

export interface Invariant {
  name: string
  kind: string
  table: string
  definition: string
}

export interface PagedResult<T> {
  items: T[]
  total: number
  pageNumber: number
  pageSize: number
  totalPages: number
  hasNext: boolean
  hasPrevious: boolean
}

export interface ProcessingEvent {
  id: string
  timestamp: string
  category: string
  module: string
  operation: string
  message: string
  status: string
  entity: string | null
  entityId: string | null
  tenantId: string | null
  correlationId: string | null
  durationMs: number | null
  sql: string | null
}

/** Erro da API já traduzido para consumo pela interface. */
export class ApiError extends Error {
  readonly status: number
  readonly code?: string
  readonly fieldErrors?: Record<string, string[]>

  constructor(
    status: number,
    message: string,
    code?: string,
    fieldErrors?: Record<string, string[]>,
  ) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.code = code
    this.fieldErrors = fieldErrors
  }
}

export interface LastRequest {
  method: string
  path: string
  status: number
  correlationId: string | null
  durationMs: number
}

let lastRequest: LastRequest | null = null
const listeners = new Set<(value: LastRequest) => void>()

export function onRequest(listener: (value: LastRequest) => void) {
  listeners.add(listener)
  return () => {
    listeners.delete(listener)
  }
}

interface RequestOptions {
  tenantId?: string
  method?: string
  body?: unknown
}

async function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const { tenantId, method = 'GET', body } = options
  const started = performance.now()

  const headers: Record<string, string> = { Accept: 'application/json' }
  // Provisório: enquanto não há autenticação, o tenant viaja por cabeçalho para
  // permitir alternar de corretora. Passa a vir do claim do token com o login.
  if (tenantId) headers['X-Tenant-Id'] = tenantId
  if (body !== undefined) headers['Content-Type'] = 'application/json'

  const response = await fetch(`${BASE_URL}${path}`, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
  })

  const durationMs = Math.round(performance.now() - started)
  lastRequest = {
    method,
    path,
    status: response.status,
    correlationId: response.headers.get('X-Correlation-Id'),
    durationMs,
  }
  listeners.forEach((listener) => listener(lastRequest!))

  if (response.status === 204) return undefined as T

  const payload = await response.json().catch(() => null)

  if (!response.ok) {
    // 422 traz erros por campo; os demais trazem mensagem e código de negócio
    const fieldErrors = payload?.errors as Record<string, string[]> | undefined
    const message =
      payload?.message ??
      payload?.title ??
      (fieldErrors ? 'Verifique os campos destacados.' : `Falha ${response.status}`)

    throw new ApiError(response.status, message, payload?.code, fieldErrors)
  }

  return payload as T
}

export const api = {
  brokerages: () => request<Brokerage[]>('/api/brokerages'),
  brokers: (tenantId: string) => request<Broker[]>('/api/brokers', { tenantId }),
  dashboard: (tenantId: string) => request<DashboardSummary>('/api/dashboard', { tenantId }),

  customers: (tenantId: string, params: {
    search?: string; kind?: string; status?: string
    includeDeleted?: boolean; page?: number; pageSize?: number
  } = {}) => {
    const query = new URLSearchParams()
    if (params.search) query.set('search', params.search)
    if (params.kind) query.set('kind', params.kind)
    if (params.status) query.set('status', params.status)
    if (params.includeDeleted) query.set('includeDeleted', 'true')
    query.set('page', String(params.page ?? 1))
    query.set('pageSize', String(params.pageSize ?? 20))
    return request<PagedResult<Customer>>(`/api/customers?${query}`, { tenantId })
  },

  customerById: (tenantId: string, id: string) =>
    request<Customer>(`/api/customers/${id}`, { tenantId }),

  createCustomer: (tenantId: string, input: CustomerInput) =>
    request<{ id: string }>('/api/customers', { tenantId, method: 'POST', body: input }),

  updateCustomer: (tenantId: string, id: string, input: Omit<CustomerInput, "document" | "kind">) =>
    request<{ id: string }>(`/api/customers/${id}`, { tenantId, method: 'PUT', body: input }),

  deleteCustomer: (tenantId: string, id: string, reason: string) =>
    request<{ id: string }>(`/api/customers/${id}`, {
      tenantId, method: 'DELETE', body: { reason },
    }),

  restoreCustomer: (tenantId: string, id: string) =>
    request<{ id: string }>(`/api/customers/${id}/restore`, { tenantId, method: 'POST' }),

  policies: (tenantId: string) => request<Policy[]>('/api/policies?limit=50', { tenantId }),
  schema: () => request<SchemaStats>('/api/engineering/schema'),
  rls: () => request<RlsPolicy[]>('/api/engineering/rls'),
  invariants: () => request<Invariant[]>('/api/engineering/invariants'),
  recentEvents: () => request<ProcessingEvent[]>('/api/events/recent'),
}

/** Tenta acessar um recurso com o tenant errado — demonstração de isolamento. */
export async function probeCrossTenant(tenantId: string, customerId: string) {
  const started = performance.now()
  const response = await fetch(`${BASE_URL}/api/customers/${customerId}`, {
    headers: { 'X-Tenant-Id': tenantId },
  })
  return {
    status: response.status,
    durationMs: Math.round(performance.now() - started),
    correlationId: response.headers.get('X-Correlation-Id'),
  }
}

/** Conecta ao stream SSE do Live Processing Console. */
export function connectEventStream(
  onEvent: (event: ProcessingEvent) => void,
  onStateChange: (state: 'connecting' | 'open' | 'closed') => void,
): () => void {
  onStateChange('connecting')
  const source = new EventSource(`${BASE_URL}/api/events/stream`)

  source.onopen = () => onStateChange('open')

  // O servidor nomeia o evento pela categoria, então onmessage não captura:
  // é preciso escutar cada tipo explicitamente.
  const categories = [
    'ApplicationLog', 'DomainEvent', 'DatabaseQuery', 'Transaction',
    'AuthorizationDecision', 'RowLevelSecurity', 'CacheEvent', 'OutboxEvent',
    'BackgroundJob', 'IntegrationEvent', 'AuditEvent', 'SecurityEvent',
    'AiAgentEvent', 'Error', 'Retry', 'CircuitBreaker',
  ]

  const handler = (event: MessageEvent) => {
    try {
      onEvent(JSON.parse(event.data) as ProcessingEvent)
    } catch {
      // Linha malformada não deve derrubar o console
    }
  }

  categories.forEach((category) => source.addEventListener(category, handler))
  source.onmessage = handler

  source.onerror = () => {
    // O EventSource reconecta sozinho; sinalizamos o estado para a interface
    onStateChange(source.readyState === EventSource.CLOSED ? 'closed' : 'connecting')
  }

  return () => {
    categories.forEach((category) => source.removeEventListener(category, handler))
    source.close()
    onStateChange('closed')
  }
}
