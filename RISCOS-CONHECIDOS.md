# Riscos conhecidos e pendências

Levantado por leitura linha a linha do código e por engenharia reversa dos
binários e da base de produção reais (`BScript.exe`, `BEXE.FDB`, `BScript.Ini` e
os scripts DDL). Última revisão: **03/09/2026** — nesta revisão, o ciclo
completo (Fase 1 → Fase 3 → Fase 4, com Fase 2 simulada) rodou de ponta a ponta
pela primeira vez **passando pela API real** (não uma cópia manual de pacote) e
como serviço Windows instalado de verdade, contra a cópia real de 366 tabelas
(`JUNIOR_.FDB`) + `BEXE.FDB`; e o formato gravado em `EXECUTAVEIS` foi corrigido
depois de comparado campo a campo contra um `BEXE.fdb` real e correto
(`BEXE_certo.FDB`) -- ver os itens de 03/09/2026 abaixo.

---

## ✅ Item 1 resolvido em 31/08/2026: `BScript.exe` substituído por `ScriptRunnerService`

**Confirmado por teste real, duas vezes** (banco de teste vazio e uma cópia de
produção real com 366 tabelas e a tabela `SCRIPTS` já populada com 2375
registros): `BScript.exe /silent /db="..."` **ignora o `/silent`**. Em ambos os
casos ele abriu a janela `Scripts` do Delphi/FireDAC e ficou parado esperando
clique — mesmo com banco correto e `SCRIPTS` populada. Rodando dentro de um
serviço Windows (sessão 0, sem desktop interativo), essa janela ficaria
invisível e travada até o timeout de 10 min matar o processo.

**Solução:** [`Services/ScriptRunnerService.cs`](Services/ScriptRunnerService.cs)
substitui a chamada ao `BScript.exe` na Fase 3. Ele:

- Varre `PastaPacotes` recursivamente atrás de `*.sql` (mesmo padrão que a Fase 4
  já usa para `*.exe`).
- Reaproveita a própria tabela `SCRIPTS` que o `BScript.exe` já mantém —
  confirmada por engenharia reversa do banco real: `ID`, `NOME_ARQUIVO`
  (varchar 255), `TIPO_EXECUCAO` (varchar 10, valores `automatica`/`manual`),
  `DATA_EXECUCAO`, com gerador `SEQUENCIA_SCRIPTS`. Isso mantém compatível o
  histórico que o `BScript.exe` já gravou manualmente em cada cliente — o
  agente nunca reaplica um script que uma pessoa já rodou pela tela.
- Roda cada script num processo `isql` **isolado** — testado que encadear
  vários arquivos numa sessão `isql` só é frágil: um script de trigger/procedure
  sem o próprio `SET TERM` (terminador de comando redefinido pro corpo com `;`
  interno) desalinha o parser e derruba todo o resto do lote. Isolado por
  processo, só aquele arquivo falha.
- Pára no primeiro script que falhar (não continua fora de ordem) e deixa a
  exceção subir pro `catch` de `ProcessarAtualizacao` em [Worker.cs](Worker.cs)
  — mesmo caminho de rollback que já existia para falha do `BScript.exe`.

**Testado contra uma cópia real e completa** (`JUNIOR_.FDB` de um cliente, 366
tabelas): de 2302 scripts encontrados recursivamente em `Scripts-BVendas`
(a pasta tem subpastas por ano — `scripts2012`...`scripts2016`, `Cria Domains`,
`scriptsComAcentuacao` etc. — não é só o que está solto na raiz), 201 pendentes
aplicaram limpo e ficaram registrados em `SCRIPTS` com `TIPO_EXECUCAO=automatica`.

**Ressalvas que sobraram, não resolvidas pelo `ScriptRunnerService`:**

- **Deriva entre nome de script e schema real.** Vários scripts de 2005-2011
  falham com "already exists"/"table unknown" mesmo nunca tendo sido logados em
  `SCRIPTS` — porque a mudança já foi aplicada, só que antes de existir esse
  controle (ex.: script cria domain `MEMOTEXTO`, domain já existe; script altera
  tabela `EMPRESA`, o schema real usa `EMPRESAS`). Isso não é bug do runner: é
  histórico legado que precisa de triagem manual antes de confiar cegamente na
  automação pros scripts mais antigos.
- **Nomes de arquivo duplicados entre subpastas — corrigido.** Achado 26 nomes de
  script que aparecem em mais de uma subpasta de `Scripts-BVendas`. O agente agora
  registra em `SCRIPTS.NOME_ARQUIVO` o **caminho relativo** a partir de
  `PastaPacotes` (ex.: `Scripts-BVendas\scripts2012\X.sql`), não só o nome do
  arquivo — e checa "já aplicado" tanto pelo caminho quanto pelo nome puro, pra
  continuar batendo com entradas antigas gravadas manualmente pelo `BScript.exe`
  (que só conhece o nome, não a subpasta).
- **`isql.exe` como dependência nova.** `ScriptRunnerService` assume
  `ATUALIZADOR_ISQL_PATH` (padrão: `...\Firebird_2_5\bin\isql.exe`) — mesmo
  requisito de "Firebird instalado no servidor do cliente" que já existia para
  `gfix`/`gbak`, só que agora usando mais uma ferramenta do mesmo pacote.

**Verificação prévia de existência + não para o lote no primeiro erro (decisão
posterior, 31/08/2026):** antes de rodar um script não registrado,
`DatabaseService.VerificarObjetoDdl` faz *parse* do próprio SQL (regex sobre
`CREATE TABLE/DOMAIN/GENERATOR/TRIGGER/INDEX` e `ALTER TABLE ADD`) e confere nas
tabelas de sistema do Firebird se o objeto já existe — cobre os scripts antigos
aplicados antes de existir `SCRIPTS` sem precisar tentar rodar (e sem depender do
texto de erro do `isql`, que tem um catálogo de mensagens quebrado nesta máquina
de teste: muitos "can't format message" em vez do texto real). Um script cujo
objeto **não** existia e mesmo assim falhou é reportado à API (`SendLog "ERRO"`,
relatório com script/posição/o que a verificação prévia achou/erro do `isql`) e o
lote **continua** para o próximo — não fica registrado em `SCRIPTS`, então uma
rodada futura tenta de novo. Testado contra dois bancos reais diferentes: 43
falhas numa cópia já parcialmente atualizada por testes anteriores, 69 numa cópia
limpa direto do dump do cliente — todas isoladas, nenhuma travou o lote nem
corrompeu nada.

---

## ✅ Bugs críticos corrigidos em 28/08/2026

Registrados aqui porque explicam decisões de desenho do código atual — o layout
de pastas do `TEMP_PATH` e a flag `backupValido` existem por causa deles.

### 2. O `BScript.exe` baixado era distribuído aos terminais — **corrigido**

Quando a API mandava `script_url`, o agente salvava o arquivo em
`{TEMP_PATH}\BScript_atual.exe`, e a Fase 4 varria `{TEMP_PATH}` inteiro atrás de
`*.exe` (`SearchOption.AllDirectories`) para injetar no `BEXE.fdb`. O próprio
BScript entrava nessa varredura: era gravado na tabela `EXECUTAVEIS` e os
terminais o baixariam achando que era uma atualização do ERP.

**Correção:** o `TEMP_PATH` agora é dividido. `{TEMP_PATH}\pacotes\` recebe os
downloads e a extração, e é a **única** pasta que a Fase 4 varre; ferramentas do
agente (o BScript baixado) e os backups do `gbak` ficam na raiz, fora do alcance.
A separação é por layout de pastas, e não por uma lista em memória, porque Fase 1
e Fase 4 acontecem em ciclos diferentes — pode haver um reinício do serviço entre
elas.

Junto veio a limpeza de `{TEMP_PATH}\pacotes\` no início da Fase 1: como o
`TEMP_PATH` só era apagado no sucesso, um executável remanescente de uma tentativa
anterior seria injetado junto com os da versão nova, misturando binários de
versões diferentes nos terminais.

### 3. O rollback podia restaurar um backup de dias atrás — **corrigido**

O `TEMP_PATH` só é apagado no caminho de sucesso, então o `JUNIOR_PRE.fbk` ficava
no disco depois de uma falha. Na tentativa seguinte, se algo quebrasse *antes* do
novo `gbak` — o `SetStatusAtualizacao("PROCESSANDO")` ou o `gfix -shut` — o
`catch` encontrava o arquivo **velho** e rodava `gbak -c -replace_database`,
substituindo o banco de produção por um estado de horas ou dias antes. O mesmo
valia para um `.fbk` truncado por um `gbak` que falhasse no meio.

**Correção:** o `preBkp` antigo é apagado no início de `ProcessarAtualizacao`, e
uma flag `backupValido` só vira `true` **depois** de o `gbak` retornar com
sucesso. O bloco de rollback exige `backupValido && File.Exists(preBkp)` — ou
seja, restaura apenas um backup gerado com êxito nesta mesma tentativa.

---

## ✅ Itens 4, 5, 6 e 10 resolvidos em 31/08/2026

### 4. O backoff quase nunca entrava em ação — **corrigido**
Falha na Fase 3 → estado `ERRO` → o ciclo seguinte caía no ramo `CONCLUIDO || ERRO`,
refazia a Fase 1 com sucesso e zerava `_falhasConsecutivas` incondicionalmente. O
contador zerava antes de crescer, então o backoff protegia contra falhas de
*download*, não contra falhas da *atualização* — o inverso do pretendido.

**Correção:** em [Worker.cs](Worker.cs), `_falhasConsecutivas` só zera nesse ramo
se `statusAtual == "CONCLUIDO"` (ciclo já estava saudável). Vindo de `ERRO`, o
contador só zera de verdade dentro de `ProcessarAtualizacao`, depois de uma Fase 3
concluída com sucesso — não basta o polling HTTP ter funcionado.

### 5. "Sucesso" sem ter atualizado nada — **corrigido**
Se a extração não produzisse nenhum `.exe`, `InjetarNovosBinarios` iterava sobre
uma lista vazia, commitava, e o fluxo seguia pra `CONCLUIDO` + `SUCESSO` como se
tivesse atualizado algo.

**Correção:** `InjetarNovosBinarios` agora lança se `Directory.GetFiles(..., "*.exe", AllDirectories)`
vier vazio, antes de abrir qualquer conexão — cai no `catch` de
`ProcessarAtualizacao` como qualquer outra falha da Fase 3/4.

### 6. A reversão de versão dependia de um `.txt` que podia sumir — **corrigido**
`versao_anterior.txt` ficava em `AppDomain.CurrentDomain.BaseDirectory`. Se
sumisse ou não pudesse ser escrito, o `catch` não revertia `VERSAO_NOVA`, e o
próximo polling enxergava o cliente como "em dia" sem a atualização ter
acontecido de fato.

**Correção, exatamente a sugerida aqui antes:** nova coluna `VERSAO_ATUAL` em
`SYS_ATUALIZACAO` (`DatabaseService.GetVersaoConfirmada`/`ConfirmarVersaoAtual`),
separada de `VERSAO_NOVA` (o alvo em disputa). `VERSAO_ATUAL` só avança depois de
uma Fase 3 concluída com sucesso de verdade — se qualquer passo falhar antes
disso, ela nunca mudou, então não há nada pra "reverter": o próximo polling já
reporta a versão certa à API sozinho. Zero arquivo solto fora do banco.

### 10. A senha do Firebird na linha de comando — **corrigido**
`ArgumentList` resolvia o parsing (senha com espaço/aspas), não a exposição:
qualquer processo local lê a linha de comando de outro via Gerenciador de Tarefas
ou WMI.

**Correção:** `gfix`, `gbak` (Worker.cs) e `isql` (ScriptRunnerService.cs) agora
recebem credenciais via `ISC_USER`/`ISC_PASSWORD` no ambiente do processo
(`ProcessService.RunProcessAsync` ganhou um parâmetro `environmentVariables`),
não mais `-user`/`-password` na linha de comando. Testado contra um banco real
antes de trocar todo mundo.

---

## ✅ Achados novos de 31/08/2026, do primeiro teste de ponta a ponta

A Fase 3/4 nunca tinha rodado através do `Worker.cs` de verdade (só peças
isoladas). Rodando o ciclo completo pela primeira vez, apareceram quatro bugs que
nenhuma leitura de código pegaria:

### 11. `-shut force_0` nunca foi sintaxe válida do `gfix`
O código sempre passou `"-shut", "force_0"` como se fosse um `gfix` aceitando
"force_0" como nome de modo. Testado direto: `gfix` recusa com "Target shutdown
mode is invalid". **Correção:** `-shut multi -force 0` (dois parâmetros
separados). "multi" (manutenção multiusuário), não "full": testado que "full"
bloqueia **até o SYSDBA** — o `isql` do `ScriptRunnerService` nunca conseguiria
conectar pra aplicar os scripts. "multi" isola os terminais do ERP e mantém
acesso administrativo.

### 12. `gfix`/`gbak` com caminho puro podem resolver pra instância errada
Nesta máquina (com Firebird 2.0 *e* 2.5 instalados), `gfix "C:\...\JUNIOR.fdb"`
sem `localhost/porta:` caiu no protocolo local/XNET, que apontou pra uma
instância de ODS mais antigo do que o banco de teste — "unsupported on-disk
structure". **Correção:** todas as chamadas de `gfix`/`gbak` em `Worker.cs` agora
usam `localhost/{ATUALIZADOR_DB_PORT}:{caminho}` explícito, igual o
`DatabaseService` já fazia pra toda conexão `FbConnection`. Num cliente real com
uma única versão do Firebird isso talvez nunca aparecesse — mas o
`ATUALIZADOR_DB_PORT` configurável só faz sentido se todo mundo (não só o
`FbConnection`) o respeitar.

### 13. Connection pooling do driver .NET quebrava depois de um `gfix -shut`
O mais sério dos quatro. Uma `FbConnection` aberta e fechada **antes** do
`gfix -shut` ficava em cache no pool do `FirebirdSql.Data.FirebirdClient`; a
**próxima** chamada com a mesma connection string (ex.: `ScriptRunnerService`
tentando ler `SCRIPTS` já dentro da janela de shutdown) tentava reaproveitar essa
conexão — agora inválida — em vez de abrir uma nova, e falhava com
`"database ... shutdown"` mesmo em modo "multi" (que deveria permitir conexão
SYSDBA nova). Reproduzido isolado: conexão 1 (antes do shutdown) OK, `gfix -shut`,
conexão 2 (mesmo processo, depois do shutdown) falha. **Correção:**
`Pooling=false` na connection string do `DatabaseService` — cada método já abre e
fecha sua própria conexão por chamada, pooling nunca trouxe benefício aqui, só
esse risco durante a janela crítica da Fase 3.

### 14. `VERSAOATUALIZADA` do `BEXE.fdb` real só cabe 5 caracteres, não 20 — reaberto e resolvido de verdade em 03/09/2026
`RDB$FIELD_LENGTH` da coluna é 20, mas o *charset* é UTF8 — `RDB$CHARACTER_LENGTH`
real é **5**. Achado em 01/09/2026: o formato de versão que o painel usa hoje
(`2026.08.27`, 10 caracteres) nunca coube; `InjetarNovosBinarios` estourava
`"string right truncation"`, um erro Firebird genérico sem dizer qual coluna.

**"Correção" original (01/09/2026), baseada numa premissa errada:**
`DatabaseService.GarantirColunaVersaoAtualizada` ampliava a coluna sozinha via
`ALTER TABLE EXECUTAVEIS ALTER COLUMN VERSAOATUALIZADA TYPE VARCHAR(20)`, achando
que o problema era só limite de tamanho para uma versão mais longa.

**O que realmente estava acontecendo (achado em 03/09/2026, comparando campo a
campo contra um `BEXE.fdb` real e correto, `BEXE_certo.FDB`):**
`VERSAOATUALIZADA` nunca guardou uma versão — é um **flag de texto**
(`"True"`/`"False"`). Por isso 5 caracteres reais sempre foram suficientes
(`"False"` tem exatamente 5): a coluna nunca esteve pequena demais, o código é
que gravava a coisa errada nela (a versão do pacote, duplicando o que já vai em
`VERSAO`). O erro de truncamento de 01/09 era sintoma de gravar uma string longa
onde deveria ir um booleano curto, não de uma coluna subdimensionada.

**Correção de verdade:** `InjetarNovosBinarios` agora grava o literal `'True'`
em `VERSAOATUALIZADA` (sinaliza "tem versão nova disponível"; reverter para
`"False"` depois que os terminais baixarem é responsabilidade de outra parte do
sistema, fora deste repositório) e não amplia mais a coluna — `GarantirColunaVersaoAtualizada`
foi removida. Ver o item de 03/09/2026 "Quatro campos de `EXECUTAVEIS` gravados
errado" logo abaixo para os outros três campos encontrados na mesma comparação.

---

## ✅ Itens 7, 8 e 9 resolvidos em 31/08/2026

### 7. API quebrada contava como ciclo saudável — corrigido
`ApiService.CheckForUpdates` engolia *toda* exceção — 401 por token errado, JSON
inválido, DNS morto — e devolvia `null`. O Worker interpretava como "sem
atualização", zerava o contador de falhas e seguia batendo a cada 10s
indefinidamente. Sem log local, sem backoff, sem sinal no painel: um cliente com
token errado ficava invisível.

**Correção:** `CheckForUpdates` não engole mais a exceção — deixa subir pro catch
de `Worker.ExecuteAsync`, que já loga o erro real (`_logger.LogError`) e incrementa
`_falhasConsecutivas`. Reaproveita o backoff corrigido no item 4 em vez de criar um
mecanismo novo: uma API fora do ar agora entra no mesmo caminho de "algo está
persistentemente quebrado" que uma falha de atualização.

### 8. O `gbak` pós-atualização era indisponibilidade jogada fora — corrigido
O backup `JUNIOR_POS.fbk` rodava ainda dentro da janela de shutdown (antes do
`gfix -online`), somando até 15 minutos ao tempo em que os terminais do ERP
ficavam bloqueados — e o arquivo é apagado poucas linhas depois, no
`Directory.Delete(_tempPath, true)`; nada o lê.

**Correção:** em [Worker.cs](Worker.cs), o backup pós-atualização agora roda
*depois* do `gfix -online`, com o banco já servindo os terminais. A injeção dos
binários no `BEXE.fdb` (que é um banco separado, não depende do `JUNIOR` estar
online) também passou a rodar antes do `-online`, então o `-online` acontece assim
que os scripts terminam — a janela de shutdown ficou só do necessário.

### 9. Downloads com timeout de 100 s — corrigido
O `HttpClient` usava o padrão do .NET (100s) e `DownloadPackages` não recebia
`CancellationToken`. Um pacote grande em link de cliente ruim estourava, e o
serviço não conseguia parar durante um download.

**Correção:** `ApiService` cria o `HttpClient` com `Timeout = Timeout.InfiniteTimeSpan`
— quem cancela agora é o `CancellationToken`, propagado de
`Worker.stoppingToken` até `DownloadPackages` e `BaixarArquivoAutenticadoAsync`.
Downloads grandes não estouram mais por tempo fixo, e parar o serviço no meio de
um download funciona.

---

## ✅ Item resolvido em 01/09/2026: `SYS_ATUALIZACAO` ausente no `JUNIOR.fdb` real

A cópia real de produção inspecionada (366 tabelas) **não tem** `SYS_ATUALIZACAO`
— a tabela inteira (`STATUS`, `VERSAO_NOVA`, `VERSAO_ATUAL`, `MENSAGEM_LOG`) era
só assumida por este projeto, sem nenhuma garantia de que existiria num cliente
novo antes da primeira instalação do agente.

**Correção:** `DatabaseService.GarantirTabelaSysAtualizacao`, chamada uma vez no
arranque do `Worker` (dentro do mesmo try/catch do ciclo, então uma falha de
conexão no boot entra no backoff normal em vez de derrubar o serviço), checa
`RDB$RELATIONS` e cria a tabela — com `VERSAO_NOVA`/`VERSAO_ATUAL` em
`VARCHAR(50)`: como é este projeto que cria a tabela do zero, não há schema
legado a respeitar, então vale deixar folga confortável para o formato de
versão do painel em vez de um limite apertado — mais a linha `ID = 1` inicial
em `CONCLUIDO`. Idempotente: num
cliente que já tiver a tabela (ou na segunda execução em qualquer cliente), é um
no-op. `SCRIPTS` continua confirmada contra o banco real (`ID`, `NOME_ARQUIVO`,
`TIPO_EXECUCAO`, `DATA_EXECUCAO`, gerador `SEQUENCIA_SCRIPTS`) — ver item 1. O
`BEXE.fdb` também foi confirmado com um arquivo real (tabela `EXECUTAVEIS`) — ver
item 14.

---

## ✅ Achados de 03/09/2026: teste real via API + comparação campo a campo contra `BEXE.fdb` correto

Primeiro teste do ciclo completo passando pela API de verdade (não uma cópia
manual de pacote): baixado o pacote publicado real do B_Vendas (2026.09.01,
50.258.096 bytes, SFX 7z com stub PE + payload a partir do offset 186368),
conferido o SHA-256, extraído com um `7za.exe` obtido via pacote NuGet
`7-Zip.CommandLine` (mesmo binário oficial, sem precisar instalar nada no
sistema), e rodado através de um serviço Windows instalado de verdade
(`AgenteAtualizadorERP_TESTE`, variáveis então via registro — antes da mudança
pra `.ini` descrita abaixo). Terminou em `CONCLUIDO`, 70 segundos de execução,
69 scripts pulados por deriva de schema (bate com a faixa documentada em
"cópia limpa direto do dump do cliente" no item 1). Essa rodada revelou dois
problemas reais:

### 15. `openssl.exe`, dependência dentro do próprio pacote, era injetado como "produto novo"

O pacote real do B_Vendas trazia, além do `B_Vendas.exe` na raiz,
`Dlls-BVendas\openssl.exe` — usado internamente pelo ERP para HTTPS, não algo
a distribuir. `InjetarNovosBinarios` varria `tempPath` com
`SearchOption.AllDirectories`, então os dois eram gravados em `EXECUTAVEIS`
como se fossem duas atualizações independentes — um terminal baixaria o
`openssl.exe` achando que era o ERP.

**Correção:** a varredura agora usa `SearchOption.TopDirectoryOnly` — só
`.exe` soltos direto na raiz do pacote (a convenção real: produto solto,
dependências em subpastas) entram na injeção.

### 16. Quatro campos de `EXECUTAVEIS` gravados no formato errado

Um arquivo `BEXE.fdb` real e correto (`BEXE_certo.FDB`) foi comparado campo a
campo contra o que `InjetarNovosBinarios` gravava. Divergência em 4 dos 6
campos:

| Campo | Gravava (errado) | Formato real (confirmado) |
|---|---|---|
| `NOMEARQUIVO` | só o nome (`B_Vendas.exe`) | caminho completo no disco do cliente (`D:\Bredas\B_Vendas.exe`) |
| `HASHEXE` | SHA-256 minúsculo (64 chars) | SHA-1 **maiúsculo** (40 chars) |
| `VERSAO` | versão do pacote da API (`2026.09.01`) | versão embutida no próprio `.exe` (`FileVersion`, ex. `26.9.1.8`) |
| `VERSAOATUALIZADA` | mesma versão do pacote (duplicando `VERSAO`) | flag de texto `"True"` (ver item 14, reaberto) |

`VERSAO` foi confirmado batendo o `FileVersion` real do `B_Vendas.exe` baixado
(`26.9.1.8`, lido via `FileVersionInfo.GetVersionInfo`) contra a linha
correspondente do `BEXE_certo.FDB` (`26.8.27.14` para uma build de 27/08) —
mesmo padrão `AA.M.D.build`. `NOMEARQUIVO` passou a ser montado a partir da
pasta onde o **`BEXE.fdb` está** (não a do agente), já que é essa pasta que
reflete onde o executável de fato mora no disco do cliente, independente de
onde o `.ini` do agente aponte pra ela.

**Correção:** `InjetarNovosBinarios` reescrito para gravar os 4 campos no
formato confirmado — ver README.md, seção "Formato gravado em `EXECUTAVEIS`".

### 17. Configuração por variável de ambiente trocada por `atualizador.ini`

Instalar o serviço de teste (`sc.exe create` + variáveis via registro,
`HKLM\SYSTEM\CurrentControlSet\Services\{nome}\Environment`) exigiu elevar duas
vezes (UAC) e editar o registro na mão — inviável como processo repetível para
instalar em dezenas de clientes em campo, ainda mais por alguém sem tanta
intimidade com ferramentas de administração do Windows.

**Correção:** toda a configuração (CNPJ, sistema, token, credencial do
Firebird, caminhos) passou de `Environment.GetEnvironmentVariable("ATUALIZADOR_*")`
espalhado em 4 arquivos para `Services/ConfiguracaoAgente.cs`, que lê um único
`atualizador.ini` ao lado do executável — um arquivo que abre no Bloco de
Notas, sem precisar de elevação nem console. Caminhos de banco/trabalho/backup
ganharam default relativo à própria pasta do agente (ver item 18), então na
maioria dos clientes só 4 chaves precisam ser preenchidas de verdade (CNPJ,
SISTEMA, API_TOKEN, DB_PASSWORD) — o resto já resolve sozinho.

### 18. Backups pré/pós eram apagados no mesmo ciclo em que nasciam

`Directory.Delete(_tempPath, true)`, no caminho de sucesso de
`ProcessarAtualizacao`, apagava `JUNIOR_PRE.fbk`/`JUNIOR_POS.fbk` junto com o
resto da pasta de trabalho. Um backup que não sobrevive nem um ciclo não serve
pra disaster recovery nenhum — é exatamente o que um DBA precisaria pra
restaurar manualmente se um problema aparecesse dias depois da atualização,
quando o rollback automático do próprio agente já não se aplica mais.

**Correção:** `Worker.ArquivarBackups` move os dois arquivos para
`PASTA_BACKUPS` (fora de `PASTA_TRABALHO`, que essa mesma função limpa logo
depois) com nome único por versão + timestamp
(`JUNIOR_PRE_9.9.9_20260903_114500.fbk`), antes de qualquer limpeza. Sem
limpeza nenhuma, cada atualização bem-sucedida deixaria 2 backups novos
parados pra sempre — um `JUNIOR.fdb` real pode ter centenas de MB/GB por
cópia. `PodarBackupsAntigos` mantém só os últimos `BACKUPS_PARA_MANTER`
ciclos (padrão: 10, ou seja, até 20 arquivos — pré + pós de cada um),
apagando os mais antigos por data de criação.

---

## ⚪ Pendências de ambiente (não corrigíveis só com código)
- **Fase 2 (Delphi) não existe.** Não há nenhum `.pas`/`.dpr`. Ler `PENDENTE`,
  perguntar ao usuário e gravar `AUTORIZADO` ainda precisa ser escrito no ERP. Nos
  testes de 31/08/2026 isso foi simulado gravando `AUTORIZADO` direto no banco.
- **Rollback — testado em 31/08/2026, com uma ressalva.** `gfix -shut` → `gbak -b`
  → `gbak -c -replace_database` → `gfix -online` funcionou de ponta a ponta contra
  um banco real. Ressalva: depois do `-c -replace_database`, o banco resultante já
  fica acessível sozinho, e o `gfix -online` que vem depois costuma falhar com
  "Target shutdown mode is invalid" (não há shutdown pra desfazer) — isso está
  dentro de um `try/catch` que só loga, então não é uma falha funcional, só um log
  que pode confundir quem estiver lendo depois.
- **Idempotência dos scripts — mitigada pela reutilização de `SCRIPTS`, não
  eliminada.** Os scripts históricos não têm proteção contra reexecução
  (`IF NOT EXISTS` ou equivalente). O `ScriptRunnerService` evita reaplicar o que
  já está em `SCRIPTS`, e a verificação prévia de existência (item 1) cobre boa
  parte do que foi aplicado antes de existir esse controle — mas não é 100%: dos
  2302 scripts reais testados, entre 43 e 69 (dependendo do estado prévio do
  banco) seguem falhando por deriva de schema (nome de tabela diferente, coluna
  que já mudou de outro jeito) e precisam de triagem manual.
- **Convenção de `NOMEPRODUTO`.** A injeção usa o nome do arquivo sem extensão como
  padrão; confirmado que funciona pro `B_Vendas.exe` real (`openssl.exe` não conta
  mais, ver item 15), mas não há confirmação de que os terminais leem esse campo
  (em vez de `NOMEARQUIVO`) pra decidir o que baixar.
- **Testes de integração automatizados.** Existem 25, cobrindo `ProcessService`,
  `DatabaseService`, `ScriptRunnerService` e o ciclo completo do `Worker`
  (`AtualizadorERP.Tests/`, contra Firebird real, não mockado) — inclusive o
  formato de `EXECUTAVEIS` confirmado no item 16 e a retenção de backups do
  item 18. Ainda não cobrem: Fase 1 completa contra a API real (o teste de
  03/09/2026 que validou isso foi manual, não faz parte da suíte), nem os
  ~2300 scripts reais de um `Scripts-BVendas` de produção (os testes usam
  scripts sintéticos pequenos) — vale repetir o ciclo completo manual contra
  cópias descartáveis dos bancos reais antes de qualquer mudança futura maior
  no `Worker.cs`/`DatabaseService.cs`.
