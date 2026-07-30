# Segredos locais

Este diretório contém segredos **apenas para desenvolvimento local**, injetados como Docker
secrets em runtime — nunca embutidos na imagem (RNF-004).

`db_password.txt` é um valor de desenvolvimento e não protege nada real. Em qualquer ambiente
que não seja a máquina do desenvolvedor, o segredo vem de um cofre externo.

O CI executa varredura de segredo (gitleaks) e falha o merge se um segredo real for commitado.
