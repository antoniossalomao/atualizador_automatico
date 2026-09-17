# Agente Atualizador ERP

Serviço Windows em C# / .NET 8 que automatiza a atualização dos sistemas do ERP
no servidor do cliente. Uma única instância detecta quais produtos estão
instalados, consulta uma API central e processa cada sistema separadamente. Para
sistemas com scripts, espera a autorização do usuário (dada pelo próprio ERP em
Delphi), isola o Firebird, aplica os scripts e distribui os executáveis. Para os
demais, copia e distribui os arquivos sem interromper o banco.

> **Estado: pré-piloto.** Compila, o fluxo principal está implementado, os bugs
> críticos conhecidos foram corrigidos, o formato gravado em `BEXE.fdb` foi
> confirmado campo a campo contra um arquivo real correto, e o ciclo completo
> (Fase 1 → Fase 3 → Fase 4, com Fase 2 simulada) já rodou de ponta a ponta
> várias vezes contra Firebird real -- ver [RISCOS-CONHECIDOS.md](RISCOS-CONHECIDOS.md).
> O `ScriptRunnerService` já foi validado contra o pacote real de scripts do
> B_Vendas (mais de 1000 arquivos, incluindo uma cópia completa de produção) e
> os problemas encontrados foram corrigidos ou isolados via `SCRIPTS_IGNORADOS`
> -- ainda assim, **não deve rodar em cliente real com sistemas que executam
> scripts**: falta a Fase 2 (autorização pelo ERP Delphi), que **não existe
> neste repositório**. Leia [RISCOS-CONHECIDOS.md](RISCOS-CONHECIDOS.md) antes
> de qualquer coisa -- é o documento mais importante deste repositório.

## Como funciona

O agente é um `BackgroundService` que faz polling para cada sistema instalado e
reage ao campo `STATUS` da linha correspondente na tabela `SYS_ATUALIZACAO`, no
`JUNIOR.fdb` do cliente. A presença do executável configurado ao lado do
`BEXE.fdb` determina se um sistema está instalado.

| Estado | Significado | Quem grava |
|---|---|---|
| `CONCLUIDO` / `ERRO` | Ocioso — livre para procurar versão nova | Agente |
| `PENDENTE` | Pacote baixado, esperando o usuário autorizar | Agente |
| `AUTORIZADO` | Usuário confirmou; pode executar | **ERP Delphi** |
| `PROCESSANDO` | Execução crítica em andamento | Agente |

Se o agente for derrubado no meio de uma Fase 3/4 (queda de energia, serviço
parado à força), `PROCESSANDO` fica gravado sem ninguém terminar o ciclo. No
próximo ciclo o próprio agente detecta isso e retoma do zero, como se fosse
`AUTORIZADO` — o processo inteiro é seguro de repetir (backup pré antigo é
descartado, scripts já aplicados são pulados).

Sistemas **sem** script (ex.: `B_NFe`) não passam pela Fase 2 (não há nada
pra autorizar), mas esperam que algum sistema **com** script deste cliente
(normalmente `B_Vendas`) tenha **concluído com sucesso** a própria Fase 3/4 —
não só sido autorizado. Só nesse
momento é que a atualização pendente de cada sistema sem script é aplicada
(troca de executável, sem tocar no `JUNIOR.fdb`). Isso evita trocar o `.exe`
de um sistema sem coordenação nenhuma enquanto um terminal pode estar com ele
aberto; clientes sem nenhum sistema com script instalado não têm essa espera
(aplicam direto, já que não existe nenhuma janela de manutenção pra
esperar).

### Fase 1 — Preparo invisível
Consulta a API com código do cliente, sistema e versão atual; se houver versão
nova, baixa os pacotes, confere o **SHA-256** de cada um e extrai com o
`7za.exe`. Sistemas listados em `SISTEMAS_COM_SCRIPT` passam para `PENDENTE`;
os demais seguem direto para a distribuição, sem `gfix`, `gbak` ou execução de
SQL.

### Fase 2 — Decisão do usuário (somente sistemas com scripts)
**Não implementada neste repositório.** Cabe ao ERP Delphi ler `PENDENTE`,
perguntar ao usuário e gravar `AUTORIZADO`. Sem isso, a atualização daquele
sistema permanece pendente. Nos testes registrados em RISCOS-CONHECIDOS.md,
essa fase é simulada gravando `AUTORIZADO` direto no banco via `isql`.

### Fase 3 — Execução crítica
`gfix -shut multi -force 0` (isola o banco, mantendo acesso SYSDBA) → `gbak`
(backup pré) → `ScriptRunnerService` (aplica cada `.sql` pendente do pacote, num
processo `isql` isolado por arquivo — ver
[RISCOS-CONHECIDOS.md](RISCOS-CONHECIDOS.md)) → injeção dos binários no
`BEXE.fdb` → `gfix -online` → `gbak` (backup pós, já com o banco de volta ao ar).

O lote de scripts roda **duas vezes** de ponta a ponta: alguns scripts
legados dependem de um objeto que só é criado por outro script mais adiante
na mesma leva (ordem alfabética do nome do arquivo nem sempre bate com ordem
de dependência real). A segunda passada só tenta de novo o que ainda não
ficou registrado como aplicado na primeira — o que já aplicou é pulado sem
rodar `isql` de novo.

### Fase 4 — Distribuição
Grava os executáveis novos como BLOB na tabela `EXECUTAVEIS` do `BEXE.fdb`
(transação única), devolve o banco com `gfix -online`, marca `CONCLUIDO` e
reporta à API. Os terminais leem o `BEXE.fdb` e se atualizam sozinhos.

Só os `.exe` soltos na **raiz** do pacote entram nessa injeção (não os de
subpastas) — ver "Formato gravado em `EXECUTAVEIS`" abaixo.

## Onde o agente mora

O agente é instalado **dentro da própria pasta do cliente** (a pasta onde já
ficam `JUNIOR.fdb`, `BEXE.fdb` e os executáveis do ERP — ex.: `Bredas\`), numa
subpasta própria, para não espalhar `.dll`/`.pdb`/`7za.exe`/backups soltos no
meio dos arquivos do cliente:

```
Bredas\                     <- pasta do cliente (já existe hoje)
  JUNIOR.fdb
  BEXE.fdb
  B_Vendas.exe, ...
  Atualizador\               <- pasta do agente (nova)
    AtualizadorERP.exe
    7za.exe
    atualizador.ini          <- configuração deste cliente (não versionado)
    _trabalho\                <- descartável, recriada a cada ciclo
      pacotes\                 <- uma subpasta por sistema em atualização
        B_Vendas\
        B_NFe\
    Backups\                  <- PERSISTENTE, nunca apagada pela limpeza automática
      JUNIOR_PRE_B_Vendas_9_9_9_20260903_114500.fbk
      JUNIOR_POS_B_Vendas_9_9_9_20260903_114500.fbk
```

Por padrão (sem nada de caminho preenchido no `.ini`), `JUNIOR.fdb`/`BEXE.fdb`
são resolvidos como `..\JUNIOR.FDB`/`..\BEXE.FDB` a partir da pasta do agente —
ou seja, um nível acima, na pasta do cliente. `PASTA_TRABALHO`/`PASTA_BACKUPS`
ficam dentro da própria pasta do agente. Qualquer um desses caminhos aceita
override explícito no `.ini`, para o cliente cuja estrutura fugir do padrão.

Cada sistema usa `_trabalho\pacotes\{sistema}\`, apagada e recriada quando uma
nova versão daquele sistema é baixada. Todo o conteúdo extraído é copiado para a
pasta do cliente, preservando as subpastas; somente os `.exe` soltos na raiz do
pacote do sistema são injetados no `BEXE.fdb`. `Backups\` nunca é tocada pela
limpeza automática de `_trabalho`;
ela mesma se poda sozinha, mantendo só os últimos `BACKUPS_PARA_MANTER` ciclos
(padrão: 10 — ver `Configuração`).

Qualquer exceção na Fase 3 ou 4 dispara o `catch`: tenta restaurar o backup pré
com `gbak -c -replace_database`, força o banco de volta ao ar e grava `ERRO` com
a mensagem em `MENSAGEM_LOG`. `VERSAO_ATUAL` só avança no caminho de sucesso,
portanto uma falha não exige reversão de versão.

## Requisitos

- **.NET 8 SDK** na máquina que compila — só isso. O executável é publicado
  como *self-contained + single-file* (ver "Compilar e publicar" abaixo): o
  runtime do .NET inteiro, mais todas as DLLs de dependência (driver do
  Firebird, `Microsoft.Extensions.*`), ficam embutidos dentro do próprio
  `AtualizadorERP.exe` — o servidor do cliente **não precisa** ter .NET
  instalado, nem sobra DLL solta ao lado do executável.
- **Firebird 2.5** instalado no servidor do cliente, com `gfix.exe`, `gbak.exe`
  e `isql.exe`
- **`7za.exe`** ao lado do executável. O pacote gerado pelo CI
  ([.github/workflows/build.yml](.github/workflows/build.yml)) já inclui essa
  cópia automaticamente — use esse pacote para instalar num cliente. Se
  compilar localmente com `dotnet publish` (ver abaixo), precisa colocar o
  `7za.exe` você mesmo ao lado do `.exe` publicado (baixe em
  [7-zip.org/download.html](https://www.7-zip.org/download.html), pacote
  "7-Zip Extra", ou extraia de um pacote NuGet como `7-Zip.CommandLine`, que
  já vem em `.zip` puro). Sem ele o ciclo aborta de propósito, em vez de
  marcar uma atualização como pronta sem os arquivos.
- Acesso de leitura/escrita ao `JUNIOR.fdb` e ao `BEXE.fdb`

## Configuração

Tudo vem de um arquivo `atualizador.ini` **ao lado do executável** — não há
nada configurável no código, e nenhuma credencial embutida. Comece copiando
[atualizador.ini.example](atualizador.ini.example) (incluído em toda
publicação) para `atualizador.ini` e preenchendo os valores.

Um `.ini` foi escolhido em vez de variável de ambiente porque configurar a
variável de ambiente de um *serviço Windows* exige elevar e editar o registro
(`HKLM\SYSTEM\CurrentControlSet\Services\{nome}\Environment`) — inviável pra
quem instala isso em campo, em dezenas de clientes. Um `.ini` abre no Bloco de
Notas.

| Chave | Padrão | Obrigatória |
|---|---|:-:|
| `CODIGO_CLIENTE` | — | **sim** |
| `SISTEMAS` | — | **sim** |
| `SISTEMAS_COM_SCRIPT` | vazio | |
| `SCRIPTS_IGNORADOS` | preenchido (scripts legados quebrados do B_Vendas) | |
| `API_TOKEN` | — | **sim** |
| `DB_PASSWORD` | — | **sim** |
| `API_URL` | `http://localhost:3000/api` | |
| `DB_USER` | `SYSDBA` | |
| `DB_PORT` | `3050` | |
| `JUNIOR_FDB` | `..\JUNIOR.FDB` (relativo à pasta do agente) | |
| `BEXE_FDB` | `..\BEXE.FDB` (relativo à pasta do agente) | |
| `GFIX_PATH` | `C:\Program Files (x86)\Firebird\Firebird_2_5\bin\gfix.exe` | |
| `GBAK_PATH` | `...\bin\gbak.exe` | |
| `ISQL_PATH` | `...\bin\isql.exe` | |
| `PASTA_TRABALHO` | `_trabalho` (dentro da pasta do agente) | |
| `PASTA_BACKUPS` | `Backups` (dentro da pasta do agente) | |
| `BACKUPS_PARA_MANTER` | `10` | |

Faltando qualquer uma das obrigatórias, o agente falha ao subir — é
intencional, para não rodar meio configurado. Também falha ao subir se
`DB_PORT` não for um número, ou se `GFIX_PATH`/`GBAK_PATH`/`ISQL_PATH`
apontarem pra um arquivo que não existe — melhor descobrir isso na
inicialização do que no meio de uma janela de manutenção real, com o banco já
em shutdown. `atualizador.ini` **nunca** deve ser commitado (tem credencial de
verdade); já está coberto pelo `.gitignore` deste repositório (`*.ini`). O
`.example`, sem segredo nenhum, é o único dos dois que fica versionado.

`API_TOKEN` precisa bater com o `AGENT_API_TOKEN` do servidor.

`CODIGO_CLIENTE` não precisa ser uma CNPJ de verdade — é só um identificador
livre (a API/banco do servidor chamam esse campo de "cnpj" por herança
histórica do projeto, mas nunca validam formato). Recomendado usar o mesmo
`codigo` já cadastrado na aba **Clientes** do painel (ex.: `C012345`): o
painel casa esse valor com o cliente automaticamente (removendo pontuação dos
dois lados) e mostra nome/cidade nos logs; qualquer outro valor não-vazio
também funciona, só aparece cru em vez do nome da empresa.

`SISTEMAS` contém todos os produtos distribuídos pela empresa no formato
`NomeNoPainel:Executavel.exe`, separados por vírgula. O nome precisa bater,
letra por letra, com o cadastro da aba **Sistemas** (ex.:
`B_Vendas:B_Vendas.exe,B_NFe:B_NFE.exe`). A mesma lista completa pode ser usada
em todos os clientes: a cada ciclo, o agente verifica quais executáveis existem
ao lado do `BEXE.fdb` e só consulta os sistemas encontrados. Uma única instância
cuida de todos eles, mantendo estado e pasta de trabalho separados por sistema.

`SISTEMAS_COM_SCRIPT` é a lista opcional dos sistemas autorizados a executar
SQL no `JUNIOR.fdb`. Eles usam o fluxo completo com `PENDENTE`, autorização do
ERP, shutdown e backups. Qualquer sistema ausente dessa lista segue o fluxo de
troca de arquivos, mesmo que seu pacote contenha `.sql`; essa decisão nunca é
inferida pelo conteúdo do pacote. Veja os exemplos comentados em
[atualizador.ini.example](atualizador.ini.example).

`SCRIPTS_IGNORADOS` é a lista opcional (por nome de arquivo, sem caminho) de
scripts `.sql` que o agente nunca deve tentar rodar, mesmo pendentes — pra
scripts legados quebrados de origem (corpo de procedure salvo sem o próprio
cabeçalho, nome de tabela que não bate mais com o schema real etc.), onde
nenhuma correção automática resolve porque o arquivo em si está incompleto ou
desatualizado. Nunca roda, nunca reporta erro, fica pendente pra sempre até
alguém corrigir o arquivo de origem e tirar da lista. O `.example` já vem com
um conjunto de scripts legados do próprio B_Vendas confirmados quebrados
rodando o pacote real de scripts contra uma cópia de produção — o mesmo
histórico se repete em qualquer instalação do produto. Se um cliente
específico corrigir a própria cópia de um desses arquivos, tire o nome dessa
lista no `.ini` **desse cliente** (não no `.example`).

> A porta `3050` é o padrão do Firebird, mas ambientes reais usam outras — um
> `BScript.Ini` de produção inspecionado usava `3051`. Confira antes.

## Compilar e publicar

O jeito oficial de publicar — o mesmo que
[.github/workflows/build.yml](.github/workflows/build.yml) usa a cada push —
é *self-contained + single-file*:

```bash
dotnet build AtualizadorERP.csproj
dotnet publish AtualizadorERP.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./Atualizador
```

Isso produz só 3 arquivos em `Atualizador/`: `AtualizadorERP.exe` (~70 MB, com
tudo embutido), `AtualizadorERP.pdb` e `atualizador.ini.example` (copiado
sozinho, configurado no `.csproj`). Copie o `7za.exe` pra dentro também (ver
"Requisitos" acima — **quem instala em cliente não precisa desse passo manual**,
ver abaixo).

> Publicando localmente mais de uma vez **na mesma pasta de saída**, sem
> limpar `bin/`/`obj/` entre uma e outra: já reproduzido um caso em que o
> `atualizador.ini.example` some sozinho na segunda publicação (limpeza
> incremental do MSBuild tratando o arquivo como "saída obsoleta" por engano).
> Não afeta o pacote do CI (cada run começa de um checkout limpo, uma
> publicação só) — só apague `bin/`, `obj/` e a pasta de saída antes de
> publicar de novo localmente se for testar isso na mão.

### Instalando num cliente

Cada push na `main` publica um instalador pronto em
`https://github.com/<org>/<repo>/releases/latest` (release `latest`, sempre
sobrescrita — sempre é o build mais recente). Baixe
`InstalarAtualizadorERP.exe`, jogue dentro da pasta do cliente (ao lado de
`JUNIOR.fdb`/`BEXE.fdb`, ver "Onde o agente mora") e execute: ele extrai a
pasta `Atualizador\` ali do lado sozinho — sem precisar descompactar nada na
mão nem baixar o `7za.exe` à parte (já vem embutido no pacote).

É um `.exe` autoextraível de verdade (módulo SFX do 7-Zip + o `.7z` do pacote
concatenados em binário — ver o passo "Montar o instalador" em
[build.yml](.github/workflows/build.yml)), não um artefato do GitHub Actions:
artefato do Actions sempre vem embrulhado num `.zip` extra ao baixar (sem como
desligar isso), o que dava arquivo compactado dentro de outro compactado.

Depois de extraído, renomeie `atualizador.ini.example` pra `atualizador.ini`,
preencha, e registre o serviço:

```powershell
sc.exe create "AgenteAtualizadorERP" binPath= "C:\caminho\ate\Bredas\Atualizador\AtualizadorERP.exe" start= auto
sc.exe start "AgenteAtualizadorERP"
```

O nome que aparece no gerenciador de serviços (`Agente Atualizador ERP`) é
definido em [Program.cs](Program.cs).

## Contrato da API

Todas as chamadas mandam o header `X-Agent-Token`.

**`GET {API_URL}/update/check/{codigoCliente}?sistema={sistema}&versao={versaoAtual}`**

```json
{
  "update_available": true,
  "version": "2026.08.10",
  "packages": [
    { "file": "pacote.7z", "url": "https://.../api/update/packages/pacote.7z", "sha256": "abc123..." }
  ],
  "script_url": "https://.../BScript.exe"
}
```

`update_available: false` quando não há nada novo. `script_url` ainda existe no
contrato por compatibilidade, mas o agente não lê mais esse campo — a Fase 3
aplica os `.sql` do próprio pacote via `ScriptRunnerService`, não mais um
binário externo (ver [RISCOS-CONHECIDOS.md](RISCOS-CONHECIDOS.md)).

**`POST {API_URL}/update/log`** — `{ "cnpj": "...", "sistema": "...", "status": "SUCESSO|ERRO", "detalhes": "..." }`
(best-effort: falha de rede aqui não interrompe nada).

## Formato gravado em `EXECUTAVEIS` (`BEXE.fdb`)

Confirmado campo a campo contra um `BEXE.fdb` real e correto
(`BEXE_certo.FDB`, 03/09/2026) — divergir de qualquer um destes formatos faz o
atualizador interno (o que os terminais rodam) não reconhecer a linha:

| Campo | Formato |
|---|---|
| `NOMEARQUIVO` | Caminho **completo** no disco do cliente (ex.: `D:\Bredas\B_Vendas.exe`) — a pasta usada é a mesma onde o `BEXE.fdb` está, não a do agente. Também é a chave usada para decidir `UPDATE` vs `INSERT`. |
| `HASHEXE` | SHA-1 em **hexadecimal maiúsculo** (40 caracteres) — não SHA-256. |
| `VERSAO` | A versão **embutida no próprio executável** (`FileVersion`, ex.: `26.9.1.8`), não a versão do pacote publicada no painel. |
| `VERSAOATUALIZADA` | Um **flag de texto** (`"True"`/`"False"`), não uma versão — é por isso que a coluna real só cabe 5 caracteres (`RDB$CHARACTER_LENGTH = 5`, `"False"` tem 5). O agente só grava `"True"` (uma versão nova está disponível); a reversão para `"False"` é responsabilidade de outra parte do sistema, fora deste repositório. |
| `EXECUTAVEL` | BLOB com o conteúdo binário completo do `.exe`. |
| `DATA_ATUALIZACAO` | Data (sem hora) da injeção. |

## Organização do código

```
Program.cs                        host do serviço Windows + injeção de dependência
Worker.cs                         o ciclo: polling, decisão de fase, orquestração das 4 fases
Services/ConfiguracaoAgente.cs    lê e valida atualizador.ini, resolve caminhos relativos
Services/ApiService.cs            HTTP com a API central, validação de SHA-256
Services/DatabaseService.cs       Firebird: estado em SYS_ATUALIZACAO, injeção de BLOB no BEXE
Services/ExtractionService.cs     invoca o 7za.exe sobre os pacotes baixados
Services/ScriptRunnerService.cs   aplica os .sql pendentes do pacote via isql, um processo por arquivo, em 2 passadas
Services/ProcessService.cs        executa processos externos com timeout obrigatório
```

**Toda chamada a processo externo passa pelo `ProcessService` e exige timeout.**
Isso não é estilo, é segurança: a Fase 3 roda com o banco em
`-shut multi -force 0` (terminais bloqueados, acesso administrativo mantido),
então um processo que trava sem timeout deixaria o cliente inteiro parado até
alguém perceber. Ver
[RISCOS-CONHECIDOS.md](RISCOS-CONHECIDOS.md).

O schema real do `BEXE.fdb` (tabela `EXECUTAVEIS`) foi confirmado por engenharia
reversa de um arquivo de produção, e o formato gravado em cada campo foi
confirmado contra uma cópia correta (ver seção acima). A tabela `SYS_ATUALIZACAO`
do `JUNIOR.fdb` **não existe** nesse schema real — por isso o próprio agente a
cria e garante uma linha por sistema instalado, usando `SISTEMA` como chave
primária (`DatabaseService.GarantirTabelaSysAtualizacao`).
