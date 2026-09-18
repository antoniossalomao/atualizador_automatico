# Segurança

Este agente roda **sem supervisão, no servidor do cliente, com credencial
administrativa do banco de dados dele** e com poder de parar esse banco. O
modelo de ameaça aqui não é só "alguém invadindo": é também "o próprio agente
fazendo a coisa errada sozinho, às 3h da manhã".

## Como relatar um problema

Problemas de segurança **não** devem virar issue pública. Fale direto com o
responsável pelo repositório. Se encontrar credencial ou dado de cliente
commitado, trate como incidente: rotacione primeiro, avise depois — remover um
arquivo do git **não** o remove do histórico.

## Credenciais e dados de cliente

| Segredo | Onde vive | Observação |
|---|---|---|
| Senha do Firebird (`DB_PASSWORD`) | `atualizador.ini`, no servidor do cliente | texto puro; ver abaixo |
| Token da API (`API_TOKEN`) | `atualizador.ini` | precisa bater com o `AGENT_API_TOKEN` do painel |

**O `.ini` guarda segredos em texto puro, e isso é uma decisão consciente**
([ADR-0001](docs/adr/0001-configuracao-em-ini.md)): quem consegue ler a pasta do
agente no servidor do cliente já tem acesso ao banco por outros meios. A
proteção efetiva é a permissão do sistema de arquivos daquele servidor.

O `.gitignore` cobre `*.ini`, `*.fdb`, `*.FDB`, `*.fbk` e `*.sql` **de
propósito**: a pasta de trabalho original ficava ao lado de cópias de produção,
e um `git add .` distraído mandaria banco de cliente para o GitHub.

> Há um `JUNIOR.FDB` (56 MB) na pasta do projeto, usado como referência de
> desenvolvimento. Está coberto pelo `.gitignore` e **não deve** ser
> versionado nem copiado para fora da máquina de desenvolvimento.

## O que já está no lugar

| Proteção | Onde | Por quê |
|---|---|---|
| Credencial por variável de ambiente (`ISC_USER`/`ISC_PASSWORD`) | `ProcessService` | a linha de comando é legível por qualquer processo local via Gerenciador de Tarefas/WMI |
| Argumentos em `ArgumentList`, nunca interpolados | `ProcessService` | senha com espaço/aspas quebraria o parsing — e injeção de argumento |
| Timeout obrigatório em todo processo externo | `ProcessService` | processo travado deixaria o banco do cliente parado indefinidamente |
| Conferência de SHA-256 de cada arquivo baixado | `ApiService` | pacote truncado por queda de rede chega "com sucesso" em HTTP |
| Validação estrita da configuração na subida | `ConfiguracaoAgente` | caminho errado descoberto depois do `-shut` deixa o banco bloqueado |
| Lista explícita de sistemas com script | `.ini` + `Worker` | inferir pelo conteúdo do pacote já se mostrou destrutivo |
| Backup antes de aplicar script, com poda controlada | `Worker` | é o caminho de volta quando um script falha |

## Limites conhecidos (e assumidos)

- **A comunicação com a API central pode ser HTTP puro**, se `API_URL` apontar
  para `http://`. Nesse caso, o token do agente e o conteúdo dos pacotes
  trafegam sem TLS. Em rede local é o cenário atual; para qualquer coisa que
  saia da rede interna, use `https://`.
- **O token do agente é compartilhado entre todos os clientes.** Não há
  credencial por instalação: vazou de um, vale para todos. Rotacionar exige
  tocar o `.ini` de **todos** os agentes instalados — senão eles param de
  atualizar, em silêncio do ponto de vista do painel.
- **O agente confia no servidor central.** Um servidor comprometido pode mandar
  qualquer pacote, e o SHA-256 conferido é o que o próprio servidor informou —
  ele garante integridade do download, não procedência. Não há assinatura de
  pacote.
- **O contrato com o ERP Delphi é um campo de texto num banco compartilhado.**
  Qualquer coisa com acesso de escrita ao `JUNIOR.fdb` pode escrever
  `AUTORIZADO` em `SYS_ATUALIZACAO` e disparar a fase crítica.

Nenhum desses itens é pendência de curto prazo — são as fronteiras do desenho
atual, registradas para que uma mudança de contexto (expor à internet, por
exemplo) não passe despercebida.
