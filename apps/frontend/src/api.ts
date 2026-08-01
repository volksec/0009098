const BASE_URL = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:8080'

// ---------------------------------------------------------------- sessão

export interface SessionUser {
  id: string
  name: string
  profile: 'BROKER' | 'REGULATOR'
  tenantId: string | null
  tenantName: string | null
  brokerId: string | null
}

/**
 * Token em `sessionStorage`, não em `localStorage`: some ao fechar a aba, e não fica
 * disponível para outra aba do mesmo navegador. Nenhum dos dois protege contra XSS —
 * cookie `HttpOnly` protegeria, mas exigiria lidar com CSRF, e a API é consumida por
 * origem separada. A troca está registrada aqui para não parecer descuido.
 */
const TOKEN_KEY = 'pdc.token'
const USER_KEY = 'pdc.user'

let token: string | null = sessionStorage.getItem(TOKEN_KEY)

export function currentToken(): string | null {
  return token
}

export function currentUser(): SessionUser | null {
  const raw = sessionStorage.getItem(USER_KEY)
  return raw ? (JSON.parse(raw) as SessionUser) : null
}

export function saveSession(newToken: string, user: SessionUser): void {
  token = newToken
  sessionStorage.setItem(TOKEN_KEY, newToken)
  sessionStorage.setItem(USER_KEY, JSON.stringify(user))
}

export function clearSession(): void {
  token = null
  sessionStorage.removeItem(TOKEN_KEY)
  sessionStorage.removeItem(USER_KEY)
}

/** Avisa a aplicação quando o token deixa de valer, para levar de volta ao login. */
let onUnauthorized: (() => void) | null = null
export function setUnauthorizedHandler(handler: () => void): void {
  onUnauthorized = handler
}

export interface Brokerage {
  id: string
  tradeName: string
  susepRegistration: string
  status: string
}

export interface Broker {
  userId: string
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
    fieldErrors?: Record<string, string[]>) {
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
  method?: string
  body?: unknown
  idempotencyKey?: string
  /** Só o login usa: ainda não há token para enviar. */
  anonymous?: boolean
}

async function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const { method = "GET", body, idempotencyKey, anonymous } = options
  const started = performance.now()

  const headers: Record<string, string> = { Accept: 'application/json' }
  // Tenant e ator saem do claim do token — não há mais cabeçalho para o cliente
  // escolher a corretora que quer enxergar.
  if (!anonymous && token) headers.Authorization = `Bearer ${token}`
  if (body !== undefined) headers['Content-Type'] = 'application/json'
  // Reenviar a mesma chave devolve a resposta original em vez de repetir o efeito
  if (idempotencyKey) headers['Idempotency-Key'] = idempotencyKey

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

  // Token expirado ou inválido: a sessão morre aqui, em um lugar só
  if (response.status === 401 && !anonymous) {
    clearSession()
    onUnauthorized?.()
  }

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

export const authApi = {
  login: (email: string, password: string) =>
    request<{ token: string; expiresAt: string; user: SessionUser }>(
      '/api/auth/login', { method: 'POST', body: { email, password }, anonymous: true }),

  me: () => request<SessionUser>('/api/auth/me'),

  demoAccounts: () =>
    request<{ email: string; nome: string; corretora: string }[]>(
      '/api/auth/demo-accounts', { anonymous: true }),
}

export const api = {
  brokerages: () => request<Brokerage[]>('/api/brokerages'),
  brokers: () => request<Broker[]>('/api/brokers'),
  dashboard: () => request<DashboardSummary>('/api/dashboard'),

  customers: (params: {
    search?: string; kind?: string; status?: string; brokerId?: string
    includeDeleted?: boolean; page?: number; pageSize?: number
  } = {}) => {
    const query = new URLSearchParams()
    if (params.search) query.set('search', params.search)
    if (params.kind) query.set('kind', params.kind)
    if (params.status) query.set('status', params.status)
    if (params.brokerId) query.set('brokerId', params.brokerId)
    if (params.includeDeleted) query.set('includeDeleted', 'true')
    query.set('page', String(params.page ?? 1))
    query.set('pageSize', String(params.pageSize ?? 20))
    return request<PagedResult<Customer>>(`/api/customers?${query}`)
  },

  customerById: (id: string) =>
    request<Customer>(`/api/customers/${id}`),

  createCustomer: (input: CustomerInput) =>
    request<{ id: string }>('/api/customers', { method: 'POST', body: input }),

  updateCustomer: (id: string, input: Omit<CustomerInput, "document" | "kind">) =>
    request<{ id: string }>(`/api/customers/${id}`, { method: 'PUT', body: input }),

  deleteCustomer: (id: string, reason: string) =>
    request<{ id: string }>(`/api/customers/${id}`, {
      method: 'DELETE', body: { reason },
    }),

  restoreCustomer: (id: string) =>
    request<{ id: string }>(`/api/customers/${id}/restore`, { method: 'POST' }),

  policies: () => request<Policy[]>('/api/policies?limit=50'),
  schema: () => request<SchemaStats>('/api/engineering/schema'),
  rls: () => request<RlsPolicy[]>('/api/engineering/rls'),
  invariants: () => request<Invariant[]>('/api/engineering/invariants'),
  recentEvents: () => request<ProcessingEvent[]>('/api/events/recent'),
}

/**
 * Tenta acessar um cliente pelo identificador exato, com o token da sessão corrente.
 *
 * Antes da autenticação a sonda forjava o cabeçalho de tenant; hoje isso é impossível,
 * porque o tenant sai do claim assinado. A demonstração ficou mais forte: o avaliador
 * entra como corretor de uma corretora, copia um identificador real, entra como
 * corretor de outra e cola aqui — e o recurso, que existe, some.
 */
export async function probeCrossTenant(customerId: string) {
  const started = performance.now()
  const response = await fetch(`${BASE_URL}/api/customers/${customerId}`, {
    headers: token ? { Authorization: `Bearer ${token}` } : {},
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
  onStateChange: (state: 'connecting' | 'open' | 'closed') => void): () => void {
  onStateChange('connecting')
  // O EventSource do navegador não aceita cabeçalhos: o token vai na query, que a API
  // aceita apenas nesta rota.
  const source = new EventSource(
    `${BASE_URL}/api/events/stream?access_token=${encodeURIComponent(token ?? '')}`)

  source.onopen = () => onStateChange('open')

  // O servidor nomeia o evento pela categoria, então onmessage não captura:
  // é preciso escutar cada tipo explicitamente.
  const categories = [
    'ApplicationLog', 'DomainEvent', 'DatabaseQuery', 'Transaction',
    'AuthorizationDecision', 'RowLevelSecurity', 'CacheEvent', 'OutboxEvent',
    'BackgroundJob', 'IntegrationEvent', 'AuditEvent', 'SecurityEvent',
    'Error', 'Retry', 'CircuitBreaker',
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

// ---------------------------------------------------------------- Fase 5

export interface Installment {
  id: string
  sequence: number
  amount: number
  dueDate: string
  status: string
  paidAt: string | null
  policyNumber: string
  policyId: string
  customerName: string
  isOverdue: boolean
}

export interface BillingSummary {
  pending: number
  paid: number
  overdue: number
  pendingAmount: number
  paidAmount: number
  overdueAmount: number
}

export interface Commission {
  id: string
  status: string
  amount: number
  baseAmount: number
  rateApplied: number
  ruleVersion: number
  referenceMonth: string
  createdAt: string
  releasedAt: string | null
  reversedFromId: string | null
  policyNumber: string
  policyId: string
  brokerName: string
  customerName: string
}

export interface MonthlyCommission {
  referenceMonth: string
  count: number
  total: number
  forecast: number
  released: number
  paid: number
  reversed: number
}

export interface Claim {
  id: string
  number: string
  status: string
  occurrenceDate: string
  reportedAt: string
  description: string
  estimatedAmount: number | null
  settledAmount: number | null
  decidedAt: string | null
  decisionReason: string | null
  policyNumber: string
  policyId: string
  customerName: string
  eventCount: number
}

export interface ClaimEvent {
  sequence: number
  kind: string
  description: string
  occurredAt: string
}

export interface ClaimDetail {
  claim: Claim & { coverageStart: string; coverageEnd: string }
  timeline: ClaimEvent[]
}

export const billingApi = {
  summary: () =>
    request<BillingSummary>('/api/billing/summary'),

  installments: (params: { status?: string; page?: number } = {}) => {
    const query = new URLSearchParams()
    if (params.status) query.set('status', params.status)
    query.set('page', String(params.page ?? 1))
    query.set('pageSize', '15')
    return request<PagedResult<Installment>>(`/api/billing/installments?${query}`)
  },

  pay: (id: string, method: string) =>
    request<{ id: string; status: string }>(`/api/billing/installments/${id}/pay`, {
      method: 'POST', body: { method },
    }),
}

export const commissionApi = {
  list: (params: { status?: string; page?: number } = {}) => {
    const query = new URLSearchParams()
    if (params.status) query.set('status', params.status)
    query.set('page', String(params.page ?? 1))
    query.set('pageSize', '15')
    return request<PagedResult<Commission>>(`/api/commissions?${query}`)
  },

  monthly: () =>
    request<MonthlyCommission[]>('/api/commissions/monthly'),

  release: (id: string) =>
    request<{ id: string }>(`/api/commissions/${id}/release`, { method: 'POST' }),

  reverse: (id: string, reason: string) =>
    request<{ reversalId: string }>(`/api/commissions/${id}/reverse`, {
      method: 'POST', body: { reason },
    }),
}

export const claimApi = {
  list: (params: { status?: string; page?: number } = {}) => {
    const query = new URLSearchParams()
    if (params.status) query.set('status', params.status)
    query.set('page', String(params.page ?? 1))
    query.set('pageSize', '15')
    return request<PagedResult<Claim>>(`/api/claims?${query}`)
  },

  detail: (id: string) =>
    request<ClaimDetail>(`/api/claims/${id}`),

  report: (input: {
    policyId: string; occurrenceDate: string; description: string; estimatedAmount?: number | null
  }) => request<{ id: string; number: string }>('/api/claims', { method: 'POST', body: input }),

  addEvent: (id: string, kind: string, description: string) =>
    request<{ sequence: number }>(`/api/claims/${id}/events`, {
      method: 'POST', body: { kind, description },
    }),

  decide: (id: string, input: {
    outcome: string; reason: string; settledAmount?: number | null
  }) => request<{ status: string }>(`/api/claims/${id}/decide`, { method: 'POST', body: input }),
}

// ---------------------------------------------------------------- cotação e proposta

export interface ProductVersion {
  id: string
  productId: string
  name: string
  branch: 'AUTO' | 'RESIDENTIAL' | 'LIFE' | 'TRAVEL'
  version: number
  baseRate: number
  riskSensitivity: number
  maxAcceptableRisk: number
  minInsuredValue: number
  maxInsuredValue: number
}

export interface CoverageOption {
  id: string
  productVersionId: string
  code: string
  name: string
  description: string | null
  isMandatory: boolean
  minLimit: number
  maxLimit: number
  deductibleKind: string
  deductibleAmount: number | null
  deductiblePercent: number | null
}

export interface InsurableAsset {
  id: string
  kind: string
  declaredValue: number
  label: string
  vehiclePostalCode: string | null
  propertyPostalCode: string | null
}

export interface QuotationSummary {
  id: string
  number: string
  status: string
  riskScore: number
  riskBand: string
  createdAt: string
  expiresAt: string
  rejectionReasons: string[] | null
  productName: string
  customerName: string
  fromPremium: number | null
  isExpired: boolean
  hasProposal: boolean
}

export interface QuotationPlan {
  id: string
  plan: string
  netPremium: number
  totalPremium: number
  riskMultiplier: number
  planMultiplier: number
  engineVersion: string
  factors: Record<string, number>
}

export interface QuotationCoverage {
  planId: string
  code: string
  name: string
  isMandatory: boolean
  limit: number
  premium: number
  deductibleKind: string
  deductibleAmount: number | null
  deductiblePercent: number | null
}

export interface QuotationDetail {
  quotation: QuotationSummary & { insuredValue: number }
  plans: QuotationPlan[]
  coverages: QuotationCoverage[]
  risk: { answers: Record<string, unknown>; schemaVersion: number; computedScore: number } | null
}

export interface ProposalSummary {
  id: string
  number: string
  status: string
  chosenPlan: string
  totalPremium: number
  installmentCount: number
  createdAt: string
  submittedAt: string | null
  decidedAt: string | null
  issuedAt: string | null
  quotationNumber: string
  customerName: string
  openPendencies: number
  policyNumber: string | null
  lastDecision: string | null
}

export interface UnderwritingDecision {
  version: number
  outcome: string
  reasons: string[]
  decidedAt: string
}

export interface ProposalDetail {
  proposal: ProposalSummary & {
    netPremium: number
    quotationId: string
    riskScore: number
    riskBand: string
    productName: string
    policyId: string | null
  }
  decisions: UnderwritingDecision[]
  pendencies: { id: string; code: string; description: string; openedAt: string; resolvedAt: string | null }[]
  history: { fromStatus: string | null; toStatus: string; reason: string | null; changedAt: string }[]
}

export interface QuotationInput {
  customerId: string
  assetId: string
  productVersionId: string
  coverageIds: string[]
  hasGarage: boolean
  usage: string
  driverAge: number
  previousClaims: boolean
}

export const quotationApi = {
  catalog: () =>
    request<{ products: ProductVersion[]; coverages: CoverageOption[] }>(
      '/api/products'),

  assets: (customerId: string) =>
    request<InsurableAsset[]>(`/api/customers/${customerId}/assets`),

  list: (params: { status?: string; page?: number } = {}) => {
    const query = new URLSearchParams()
    if (params.status) query.set('status', params.status)
    query.set('page', String(params.page ?? 1))
    query.set('pageSize', '15')
    return request<PagedResult<QuotationSummary>>(`/api/quotations?${query}`)
  },

  detail: (id: string) =>
    request<QuotationDetail>(`/api/quotations/${id}`),

  create: (input: QuotationInput) =>
    request<{ id: string; number: string; riskScore: number; riskBand: string }>(
      '/api/quotations', { method: 'POST', body: input }),

  convert: (id: string, plan: string, installmentCount: number) =>
    request<{ id: string; number: string }>(`/api/quotations/${id}/convert`, {
      method: 'POST', body: { plan, installmentCount },
    }),
}

export const proposalApi = {
  list: (params: { status?: string; page?: number } = {}) => {
    const query = new URLSearchParams()
    if (params.status) query.set('status', params.status)
    query.set('page', String(params.page ?? 1))
    query.set('pageSize', '15')
    return request<PagedResult<ProposalSummary>>(`/api/proposals?${query}`)
  },

  detail: (id: string) =>
    request<ProposalDetail>(`/api/proposals/${id}`),

  underwrite: (id: string, outcome: string, reason: string) =>
    request<{ version: number; status: string }>(`/api/proposals/${id}/underwrite`, {
      method: 'POST', body: { outcome, reason },
    }),

  /**
   * A chave de idempotência é gerada uma vez por tentativa e reenviada nos retries:
   * é o que permite ao usuário clicar duas vezes sem emitir duas apólices.
   */
  issue: (id: string, idempotencyKey: string) =>
    request<{ policyId: string; number: string; periodStart: string; periodEnd: string; totalPremium: number; installments: number }>(
      `/api/proposals/${id}/issue`,
      { method: 'POST', body: {}, idempotencyKey }),
}
