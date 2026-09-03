# Agente Atualizador ERP

Serviço Windows em C# / .NET 8 que automatiza a atualização do ERP no servidor
do cliente: consulta uma API central, baixa e valida os pacotes da versão nova,
espera a autorização do usuário (dada pelo próprio ERP em Delphi), isola o banco
Firebird, aplica os scripts, distribui os executáveis novos e devolve o banco ao ar.

> **Estado: pré-piloto.** Compila, o fluxo principal está implementado, os bugs
> críticos conhecidos foram corrigidos, o formato gravado em `BEXE.fdb` foi
> confirmado campo a campo contra um arquivo real correto, e o ciclo completo
> (Fase 1 → Fase 3 → Fase 4, com Fase 2 simulada) já rodou de ponta a ponta
> várias vezes contra Firebird real -- ver [RISCOS-CONHECIDOS.md](RISCOS-CONHECIDOS.md).
> Ainda **não deve rodar em cliente real**: falta a Fase 2 (autorização pelo ERP
> Delphi) e triagem dos scripts antigos que já foram aplicados fora do controle
> da tabela `SCRIPTS`. Leia [RISCOS-CONHECIDOS.md](RISCOS-CONHECIDOS.md) antes de
> qualquer coisa -- é o documento mais importante deste repositório.

## Como funciona

O agente é um `BackgroundService` que faz polling e reage ao campo `STATUS` da
tabela `SYS_ATUALIZACAO`, no `JUNIOR.fdb` do cliente.

| Estado | Significado | Quem grava |
|---|---|---|
| `CONCLUIDO` / `ERRO` | Ocioso — livre para procurar versão nova | Agente |
| `PENDENTE` | Pacote baixado, esperando o usuário autorizar | Agente |
| `AUTORIZADO` | Usuário confirmou; pode executar | **ERP Delphi** |
| `PROCESSANDO` | Execução crítica em andamento | Agente |

### Fase 1 — Preparo invisível
Consulta a API com CNPJ e versão atual; se houver versão nova, baixa os pacotes,
confere o **SHA-256** de cada um, extrai com o `7za.exe` e grava `PENDENTE`.

### Fase 2 — Decisão do usuário
**Não implementada neste repositório.** Cabe ao ERP Delphi ler `PENDENTE`,
perguntar ao usuário e gravar `AUTORIZADO`. Sem isso o ciclo trava aqui. Nos
testes registrados em RISCOS-CONHECIDOS.md, essa fase é simulada gravando
`AUTORIZADO` direto no banco via `isql`.

### Fase 3 — Execução crítica
`gfix -shut multi -force 0` (isola o banco, mantendo acesso SYSDBA) → `gbak`
(backup pré) → `ScriptRunnerService` (aplica cada `.sql` pendente do pacote, num
processo `isql` isolado por arquivo — ver
[RISCOS-CONHECIDOS.md](RISCOS-CONHECIDOS.md)) → injeção dos binários no
`BEXE.fdb` → `gfix -online` → `gbak` (backup pós, já com o banco de volta ao ar).

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
      pacotes\                 <- downloads + extração da versão em andamento
    Backups\                  <- PERSISTENTE, nunca apagada pela limpeza automática
      JUNIOR_PRE_9.9.9_20260903_114500.fbk
      JUNIOR_POS_9.9.9_20260903_114500.fbk
```

Por padrão (sem nada de caminho preenchido no `.ini`), `JUNIOR.fdb`/`BEXE.fdb`
são resolvidos como `..\JUNIOR.FDB`/`..\BEXE.FDB` a partir da pasta do agente —
ou seja, um nível acima, na pasta do cliente. `PASTA_TRABALHO`/`PASTA_BACKUPS`
ficam dentro da própria pasta do agente. Qualquer um desses caminhos aceita
override explícito no `.ini`, para o cliente cuja estrutura fugir do padrão.

`_trabalho\pacotes\` é apagada e recriada a cada Fase 1 nova, e é a **única**
pasta que a Fase 4 varre atrás de `*.exe` para injetar no `BEXE.fdb` — qualquer
`.exe` que caia ali (inclusive de terceiros, ver item sobre `openssl.exe` no
RISCOS-CONHECIDOS.md) só é considerado se estiver solto direto nela, não em
subpastas. `Backups\` nunca é tocada pela limpeza automática de `_trabalho`;
ela mesma se poda sozinha, mantendo só os últimos `BACKUPS_PARA_MANTER` ciclos
(padrão: 10 — ver `Configuração`).

Qualquer exceção na Fase 3 ou 4 dispara o `catch`: tenta restaurar o backup pré
com `gbak -c -replace_database`, força o banco de volta ao ar, grava `ERRO` com a
mensagem em `MENSAGEM_LOG` e reverte `VERSAO_NOVA` para a versão anterior.

## Requisitos

- **.NET 8 SDK** (para compilar) ou Runtime (para rodar)
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
| `CNPJ` | — | **sim** |
| `SISTEMA` | — | **sim** |
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
intencional, para não rodar meio configurado. `atualizador.ini` **nunca** deve
ser commitado (tem credencial de verdade); já está coberto pelo `.gitignore`
deste repositório (`*.ini`). O `.example`, sem segredo nenhum, é o único dos
dois que fica versionado.

`API_TOKEN` precisa bater com o `AGENT_API_TOKEN` do servidor.

`SISTEMA` precisa bater, letra por letra, com o nome de um sistema
cadastrado na aba **Sistemas** do painel web (ex.: `B_Vendas`, `B_NFe`,
`B_Ordem`) — é o mesmo catálogo que aparece no formulário de "Preparar versão"
da aba Distribuição. O painel mantém uma versão publicada **por sistema**, e o
agente só recebe pacotes do sistema que ele mesmo declara: uma instância que
atualiza o `JUNIOR.fdb`/`BEXE.fdb` do B_Vendas cuida só do B_Vendas, e uma
máquina que roda mais de um sistema precisa de uma instância do serviço por
sistema (pasta + `atualizador.ini` próprios), cada uma com seu próprio
`SISTEMA`. Sem essa chave, o agente não consegue nem consultar se há
atualização — o servidor recusa a chamada (ver
`web/docs/REVISAO_INTERFACE.md`, seção "Contrato do agente").

> A porta `3050` é o padrão do Firebird, mas ambientes reais usam outras — um
> `BScript.Ini` de produção inspecionado usava `3051`. Confira antes.

## Compilar e publicar

```bash
dotnet build AtualizadorERP.csproj
dotnet publish AtualizadorERP.csproj -c Release -r win-x64 --self-contained false -o publicado
```

`dotnet publish` já copia `atualizador.ini.example` pra dentro de `publicado/`
sozinho (configurado no `.csproj`). Copie o `7za.exe` pra dentro de
`publicado/` também (ver "Requisitos" acima — o pacote do CI já vem com isso),
copie a pasta inteira pra dentro de `Atualizador\` na pasta do cliente (ver
"Onde o agente mora"), renomeie `atualizador.ini.example` pra `atualizador.ini`
e preencha, e registre o serviço:

```powershell
sc.exe create "AgenteAtualizadorERP" binPath= "C:\caminho\ate\Bredas\Atualizador\AtualizadorERP.exe" start= auto
sc.exe start "AgenteAtualizadorERP"
```

O nome que aparece no gerenciador de serviços (`Agente Atualizador ERP`) é
definido em [Program.cs](Program.cs).

## Contrato da API

Todas as chamadas mandam o header `X-Agent-Token`.

**`GET {API_URL}/update/check/{cnpj}?versao={versaoAtual}`**

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

**`POST {API_URL}/update/log`** — `{ "cnpj": "...", "status": "SUCESSO|ERRO", "detalhes": "..." }`
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
Services/ScriptRunnerService.cs   aplica os .sql pendentes do pacote via isql, um processo por arquivo
Services/ProcessService.cs        executa processos externos com timeout obrigatório
```

**Toda chamada a processo externo passa pelo `ProcessService` e exige timeout.**
Isso não é estilo, é segurança: a Fase 3 roda com o banco em `-shut force_0`
(bloqueado para todos os usuários), então um processo que trava sem timeout
deixaria o cliente inteiro parado até alguém perceber. Ver
[RISCOS-CONHECIDOS.md](RISCOS-CONHECIDOS.md).

O schema real do `BEXE.fdb` (tabela `EXECUTAVEIS`) foi confirmado por engenharia
reversa de um arquivo de produção, e o formato gravado em cada campo foi
confirmado contra uma cópia correta (ver seção acima). A tabela `SYS_ATUALIZACAO`
do `JUNIOR.fdb` **não existe** nesse schema real — por isso o próprio agente a
cria (e insere a linha `ID = 1` inicial) no primeiro ciclo, se ainda não existir
(`DatabaseService.GarantirTabelaSysAtualizacao`, chamado uma vez no arranque do
`Worker`).
