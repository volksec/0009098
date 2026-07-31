const BASE_URL = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:8080'

export interface Brokerage {
  id: string
  tradeName: string
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
  createdAt: string
  brokerName: string
  assetCount: number
  activePolicies: number
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

/** Última requisição observada — alimenta o rodapé de rastreabilidade. */
export interface LastRequest {
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

export function getLastRequest() {
  return lastRequest
}

async function request<T>(path: string, tenantId?: string): Promise<T> {
  const started = performance.now()
  const headers: Record<string, string> = { Accept: 'application/json' }

  // Provisório desta fatia: o tenant viaja por cabeçalho para permitir alternar de
  // corretora sem um fluxo de login. Na versão com autenticação vem do claim do token.
  if (tenantId) headers['X-Tenant-Id'] = tenantId

  const response = await fetch(`${BASE_URL}${path}`, { headers })
  const durationMs = Math.round(performance.now() - started)

  lastRequest = {
    path,
    status: response.status,
    correlationId: response.headers.get('X-Correlation-Id'),
    durationMs,
  }
  listeners.forEach((listener) => listener(lastRequest!))

  if (!response.ok) throw new Error(`${response.status} ${response.statusText}`)
  return (await response.json()) as T
}

export const api = {
  brokerages: () => request<Brokerage[]>('/api/brokerages'),
  dashboard: (tenantId: string) => request<DashboardSummary>('/api/dashboard', tenantId),
  customers: (tenantId: string, search?: string) =>
    request<Customer[]>(
      `/api/customers?limit=50${search ? `&search=${encodeURIComponent(search)}` : ''}`,
      tenantId,
    ),
  customerById: (tenantId: string, id: string) =>
    request<Customer>(`/api/customers/${id}`, tenantId),
  policies: (tenantId: string) => request<Policy[]>('/api/policies?limit=50', tenantId),
  schema: () => request<SchemaStats>('/api/engineering/schema'),
  rls: () => request<RlsPolicy[]>('/api/engineering/rls'),
  invariants: () => request<Invariant[]>('/api/engineering/invariants'),
}

/** Tenta acessar um recurso com o tenant errado — usado na demonstração de isolamento. */
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
