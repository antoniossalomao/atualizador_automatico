# Agente Atualizador ERP

Serviço Windows em C# / .NET 8 que automatiza a atualização do ERP no servidor
do cliente: consulta uma API central, baixa e valida os pacotes da versão nova,
espera a autorização do usuário (dada pelo próprio ERP em Delphi), isola o banco
Firebird, aplica os scripts, distribui os executáveis novos e devolve o banco ao ar.

> **Estado: pré-piloto.** Compila, o fluxo principal está implementado, os bugs
> críticos conhecidos foram corrigidos e a Fase 3 não depende mais do
> `BScript.exe` (confirmado, em teste real, que ele não roda headless — ver
> [RISCOS-CONHECIDOS.md](RISCOS-CONHECIDOS.md)). Ainda **não deve rodar em
> cliente real**: faltam a Fase 2 (autorização pelo ERP Delphi) e triagem dos
> scripts antigos que já foram aplicados fora do controle da tabela `SCRIPTS`.
> Leia [RISCOS-CONHECIDOS.md](RISCOS-CONHECIDOS.md) antes de qualquer coisa — é
> o documento mais importante deste repositório.

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
perguntar ao usuário e gravar `AUTORIZADO`. Sem isso o ciclo trava aqui.

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

### Layout do `TEMP_PATH`

A divisão abaixo é **load-bearing**, não organização cosmética:

```
{TEMP_PATH}\pacotes\          <- downloads + extração. ÚNICA pasta que a Fase 4
                                 varre atrás de "*.exe" para injetar no BEXE.
{TEMP_PATH}\BScript_atual.exe <- ferramenta do agente, deliberadamente FORA
{TEMP_PATH}\JUNIOR_PRE.fbk    <- backups do gbak, também fora
{TEMP_PATH}\JUNIOR_POS.fbk
```

Qualquer `.exe` que caia em `pacotes\` vai parar no `BEXE.fdb` e será baixado
pelos terminais como se fosse uma atualização do ERP. Ver o item 2 de
[RISCOS-CONHECIDOS.md](RISCOS-CONHECIDOS.md).

Qualquer exceção na Fase 3 ou 4 dispara o `catch`: tenta restaurar o backup pré
com `gbak -c -replace_database`, força o banco de volta ao ar, grava `ERRO` com a
mensagem em `MENSAGEM_LOG` e reverte `VERSAO_NOVA` para a versão anterior.

## Requisitos

- **.NET 8 SDK** (para compilar) ou Runtime (para rodar)
- **Firebird 2.5** instalado no servidor do cliente, com `gfix.exe`, `gbak.exe`
  e `isql.exe`
- **`7za.exe`** ao lado do executável publicado — baixe em
  [7-zip.org/download.html](https://www.7-zip.org/download.html) (pacote "7-Zip Extra").
  Sem ele o ciclo aborta de propósito, em vez de marcar uma atualização como
  pronta sem os arquivos.
- Acesso de leitura/escrita ao `JUNIOR.fdb` e ao `BEXE.fdb`

## Configuração

Tudo vem de variável de ambiente — **não há nada configurável no código, e
nenhuma credencial embutida**. Num serviço Windows, o lugar natural é o
`Environment` do serviço ou variáveis de máquina.

| Variável | Padrão | Obrigatória |
|---|---|:-:|
| `ATUALIZADOR_API_URL` | `http://localhost:3000/api` | |
| `ATUALIZADOR_API_TOKEN` | — | **sim** |
| `ATUALIZADOR_CNPJ` | — | **sim** |
| `ATUALIZADOR_SISTEMA` | — | **sim** |
| `ATUALIZADOR_DB_PASSWORD` | — | **sim** |
| `ATUALIZADOR_DB_USER` | `SYSDBA` | |
| `ATUALIZADOR_DB_PORT` | `3050` | |
| `ATUALIZADOR_JUNIOR_FDB` | `C:\ERP\JUNIOR.fdb` | |
| `ATUALIZADOR_BEXE_FDB` | `C:\ERP\BEXE.fdb` | |
| `ATUALIZADOR_GFIX_PATH` | `C:\Program Files (x86)\Firebird\Firebird_2_5\bin\gfix.exe` | |
| `ATUALIZADOR_GBAK_PATH` | `...\bin\gbak.exe` | |
| `ATUALIZADOR_ISQL_PATH` | `...\bin\isql.exe` | |
| `ATUALIZADOR_TEMP_PATH` | `C:\TempUpdates` | |

`ATUALIZADOR_API_TOKEN` precisa bater com o `AGENT_API_TOKEN` do servidor.
Faltando qualquer uma das obrigatórias, o agente falha ao subir (`API_TOKEN`) ou
ao entrar no ciclo — é intencional, para não rodar meio configurado.

`ATUALIZADOR_SISTEMA` precisa bater, letra por letra, com o nome de um sistema
cadastrado na aba **Sistemas** do painel web (ex.: `B_Vendas`, `B_NFe`,
`B_Ordem`) — é o mesmo catálogo que aparece no formulário de "Preparar versão"
da aba Distribuição. O painel mantém uma versão publicada **por sistema**, e o
agente só recebe pacotes do sistema que ele mesmo declara: uma instância que
atualiza o `JUNIOR.fdb`/`BEXE.fdb` do B_Vendas cuida só do B_Vendas, e uma
máquina que roda mais de um sistema precisa de uma instância do serviço por
sistema, cada uma com seu próprio `ATUALIZADOR_SISTEMA`. Sem essa variável, o
agente não consegue nem consultar se há atualização — o servidor recusa a
chamada (ver `web/docs/REVISAO_INTERFACE.md`, seção "Contrato do agente").

> A porta `3050` é o padrão do Firebird, mas ambientes reais usam outras — um
> `BScript.Ini` de produção inspecionado usava `3051`. Confira antes.

## Compilar e publicar

```bash
dotnet build AtualizadorERP.csproj
dotnet publish AtualizadorERP.csproj -c Release -r win-x64 --self-contained false -o publicado
```

Copie o `7za.exe` para dentro de `publicado/` e registre o serviço:

```powershell
sc.exe create "AgenteAtualizadorERP" binPath= "C:\caminho\publicado\AtualizadorERP.exe" start= auto
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

## Organização do código

```
Program.cs                      host do serviço Windows + injeção de dependência
Worker.cs                       o ciclo: polling, decisão de fase, orquestração das 4 fases
Services/ApiService.cs          HTTP com a API central, validação de SHA-256
Services/DatabaseService.cs     Firebird: estado em SYS_ATUALIZACAO, injeção de BLOB no BEXE
Services/ExtractionService.cs   invoca o 7za.exe sobre os pacotes baixados
Services/ScriptRunnerService.cs aplica os .sql pendentes do pacote via isql, um processo por arquivo
Services/ProcessService.cs      executa processos externos com timeout obrigatório
```

**Toda chamada a processo externo passa pelo `ProcessService` e exige timeout.**
Isso não é estilo, é segurança: a Fase 3 roda com o banco em `-shut force_0`
(bloqueado para todos os usuários), então um processo que trava sem timeout
deixaria o cliente inteiro parado até alguém perceber. Ver
[RISCOS-CONHECIDOS.md](RISCOS-CONHECIDOS.md).

O schema real do `BEXE.fdb` (tabela `EXECUTAVEIS`) foi confirmado por engenharia
reversa de um arquivo de produção. A tabela `SYS_ATUALIZACAO` do `JUNIOR.fdb`
**não existe** nesse schema real — por isso o próprio agente a cria (e insere a
linha `ID = 1` inicial) no primeiro ciclo, se ainda não existir
(`DatabaseService.GarantirTabelaSysAtualizacao`, chamado uma vez no arranque do
`Worker`). Da mesma forma, `EXECUTAVEIS.VERSAOATUALIZADA` é ampliada
automaticamente para 20 caracteres reais antes de cada injeção de binários
(`GarantirColunaVersaoAtualizada`) — ver item 14 de
[RISCOS-CONHECIDOS.md](RISCOS-CONHECIDOS.md).
