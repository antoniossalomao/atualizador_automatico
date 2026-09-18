# ADR-0001 — Configuração em `.ini` ao lado do executável

**Situação:** Aceita

## Contexto

O agente roda como **serviço do Windows** no servidor de cada cliente, e precisa
de configuração que varia por instalação: código do cliente, quais sistemas
existem, token da API e credencial do Firebird.

A primeira versão usava variáveis de ambiente — o padrão em aplicações modernas
(12-factor). Na prática, isso não funcionou: definir variável de ambiente para
um serviço do Windows exige elevar privilégio e editar o registro, em
`HKLM\SYSTEM\CurrentControlSet\Services\{nome}\Environment`.

Quem instala o agente faz isso **em dezenas de clientes, em campo**, muitas
vezes por acesso remoto e com o cliente esperando.

## Decisão

A configuração mora num `atualizador.ini`, arquivo de texto ao lado do
executável publicado, lido e validado por `Services/ConfiguracaoAgente.cs` na
inicialização.

Só entra no arquivo o que varia por cliente **e** não dá para descobrir
sozinho. Caminhos de banco, backup e pasta de trabalho têm padrão relativo à
pasta do agente, com override opcional.

A validação é **estrita e na subida**: campo obrigatório ausente, porta não
numérica ou caminho de ferramenta do Firebird inexistente derrubam o serviço
imediatamente, com mensagem dizendo o que falta e como corrigir.

## Consequências

**Ganhos**
- Configurar é abrir o Bloco de Notas. Sem elevação, sem registro, sem reiniciar
  nada além do próprio serviço.
- Dá para ver a configuração atual de um cliente olhando um arquivo — e mandar
  por e-mail o `.ini` de exemplo para alguém preencher.
- `atualizador.ini.example` é publicado junto do executável, então quem instala
  tem o modelo com todas as chaves na mão.
- Falhar na subida é muito melhor que falhar no meio de um ciclo: um caminho de
  `gfix` errado descoberto depois do `-shut` deixaria o banco do cliente
  bloqueado.

**Custos aceitos**
- **O arquivo contém a senha do Firebird e o token da API em texto puro.** A
  proteção é de sistema de arquivos: quem lê a pasta do agente no servidor do
  cliente já tem acesso ao banco de qualquer jeito. Aceito conscientemente; o
  `.gitignore` cobre `*.ini` para nunca chegar ao repositório.
- Sai do padrão 12-factor, o que surpreende quem espera variáveis de ambiente.
- É mais um arquivo que pode ser perdido numa reinstalação.

## Alternativas consideradas

- **Variáveis de ambiente** — descartado: inviável operacionalmente (ver
  contexto). O custo recai justamente sobre a operação mais frequente.
- **`appsettings.json`** (padrão .NET) — descartado por pouca margem. Seria
  idiomático, mas JSON é hostil a quem edita à mão sem editor: uma vírgula
  sobrando e o serviço não sobe, com mensagem de erro de parser. `.ini` tolera
  espaço, linha em branco e comentário com `;` sem cerimônia.
- **Buscar configuração na API central** — descartado: criaria uma dependência
  circular (precisaria do token e do código do cliente para buscar a
  configuração que contém o token e o código do cliente).
