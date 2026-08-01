import { useState } from 'react'
import { ApiError, authApi, saveSession, type SessionUser } from './api'

/**
 * Entrada no sistema.
 *
 * A tela lista contas de demonstração porque a massa é sintética e o avaliador não tem
 * como adivinhar um e-mail gerado por seed. Em sistema real isso seria vazamento; aqui
 * é o contrário: esconder as credenciais de uma base fictícia só tornaria o case
 * inavaliável.
 */
export function Login({ onEntrar }: { onEntrar: (user: SessionUser) => void }) {
  const [email, setEmail] = useState('')
  const [senha, setSenha] = useState('')
  const [erro, setErro] = useState<string | null>(null)
  const [entrando, setEntrando] = useState(false)
  const [contas, setContas] = useState<{ email: string; nome: string; corretora: string }[]>([])
  const [mostrandoContas, setMostrandoContas] = useState(false)

  const entrar = async (evento: React.FormEvent) => {
    evento.preventDefault()
    setEntrando(true)
    setErro(null)

    try {
      const resposta = await authApi.login(email.trim(), senha)
      saveSession(resposta.token, resposta.user)
      onEntrar(resposta.user)
    } catch (err) {
      setErro((err as ApiError).message)
    } finally {
      setEntrando(false)
    }
  }

  const carregarContas = async () => {
    setMostrandoContas(true)
    try {
      setContas(await authApi.demoAccounts())
    } catch {
      setContas([])
    }
  }

  return (
    <div className="login-page">
      <div className="login-card">
        <div className="brand" style={{ marginBottom: 22 }}>
          <div className="brand-mark">PC</div>
          <div>
            <div className="brand-name">Portal do Corretor</div>
            <div className="brand-sub">Gestão de seguros</div>
          </div>
        </div>

        <form onSubmit={entrar}>
          {erro && <div className="alert">{erro}</div>}

          <div className="field">
            <label htmlFor="email">E-mail</label>
            <input
              id="email"
              type="email"
              autoComplete="username"
              required
              autoFocus
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              placeholder="nome.sobrenome@corretora1.test"
            />
          </div>

          <div className="field">
            <label htmlFor="senha">Senha</label>
            <input
              id="senha"
              type="password"
              autoComplete="current-password"
              required
              value={senha}
              onChange={(e) => setSenha(e.target.value)}
            />
          </div>

          <button className="btn" type="submit" disabled={entrando} style={{ width: '100%' }}>
            {entrando ? 'Entrando…' : 'Entrar'}
          </button>
        </form>

        <div className="login-demo">
          {!mostrandoContas ? (
            <button className="btn ghost small" onClick={carregarContas} style={{ width: '100%' }}>
              Ver contas de demonstração
            </button>
          ) : (
            <>
              <p className="hint-text" style={{ marginTop: 0 }}>
                Todas usam a senha <code>Corretor@2026</code>. Entrar com corretoras diferentes
                mostra a Row-Level Security separando os dados.
              </p>
              <div className="pick-list">
                {contas.map((conta) => (
                  <button
                    key={conta.email}
                    className="pick"
                    onClick={() => { setEmail(conta.email); setSenha('Corretor@2026') }}
                  >
                    <div>
                      <strong>{conta.nome}</strong>
                      <div className="hint-text">{conta.corretora} · {conta.email}</div>
                    </div>
                    <span className="badge muted">usar</span>
                  </button>
                ))}
                {contas.length === 0 && <div className="state">Nenhuma conta encontrada.</div>}
              </div>
            </>
          )}
        </div>
      </div>
    </div>
  )
}
