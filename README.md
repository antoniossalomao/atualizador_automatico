# Agente Atualizador ERP

Serviço Windows em C# / .NET 8 que automatiza a atualização do ERP no servidor
do cliente: consulta uma API central, baixa e valida os pacotes da versão nova,
espera a autorização do usuário (dada pelo próprio ERP em Delphi), isola o banco
Firebird, aplica os scripts, distribui os executáveis novos e devolve o banco ao ar.

> **Estado: pré-piloto.** Compila, o fluxo principal está implementado e os bugs
> críticos conhecidos foram corrigidos — mas **não deve rodar em cliente real
> ainda**: a automação depende de o `BScript.exe` aceitar linha de comando, o que
> não está confirmado. Leia [RISCOS-CONHECIDOS.md](RISCOS-CONHECIDOS.md) antes de
> qualquer coisa — é o documento mais importante deste repositório.

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
confere o **SHA-256** de cada um, extrai com o `7za.exe`, baixa o `BScript.exe`
da versão (se a API mandar `script_url`) e grava `PENDENTE`.

### Fase 2 — Decisão do usuário
**Não implementada neste repositório.** Cabe ao ERP Delphi ler `PENDENTE`,
perguntar ao usuário e gravar `AUTORIZADO`. Sem isso o ciclo trava aqui.

### Fase 3 — Execução crítica
`gfix -shut force_0` (isola o banco) → `gbak` (backup pré) → `BScript.exe`
(aplica os scripts) → `gbak` (backup pós).

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
- **Firebird 2.5** instalado no servidor do cliente, com `gfix.exe` e `gbak.exe`
- **`7za.exe`** ao lado do executável publicado — baixe em
  [7-zip.org/download.html](https://www.7-zip.org/download.html) (pacote "7-Zip Extra").
  Sem ele o ciclo aborta de propósito, em vez de marcar uma atualização como
  pronta sem os arquivos.
- **`BScript.exe`** no servidor do cliente, ou distribuído pela API via `script_url`
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
| `ATUALIZADOR_DB_PASSWORD` | — | **sim** |
| `ATUALIZADOR_DB_USER` | `SYSDBA` | |
| `ATUALIZADOR_DB_PORT` | `3050` | |
| `ATUALIZADOR_JUNIOR_FDB` | `C:\ERP\JUNIOR.fdb` | |
| `ATUALIZADOR_BEXE_FDB` | `C:\ERP\BEXE.fdb` | |
| `ATUALIZADOR_GFIX_PATH` | `C:\Program Files (x86)\Firebird\Firebird_2_5\bin\gfix.exe` | |
| `ATUALIZADOR_GBAK_PATH` | `...\bin\gbak.exe` | |
| `ATUALIZADOR_BSCRIPT_PATH` | `C:\ERP\BScript.exe` | |
| `ATUALIZADOR_TEMP_PATH` | `C:\TempUpdates` | |

`ATUALIZADOR_API_TOKEN` precisa bater com o `AGENT_API_TOKEN` do servidor.
Faltando qualquer uma das obrigatórias, o agente falha ao subir (`API_TOKEN`) ou
ao entrar no ciclo — é intencional, para não rodar meio configurado.

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

`update_available: false` quando não há nada novo. `script_url` é opcional — se
vier, o agente usa esse binário na Fase 3 em vez do caminho local.

**`POST {API_URL}/update/log`** — `{ "cnpj": "...", "status": "SUCESSO|ERRO", "detalhes": "..." }`
(best-effort: falha de rede aqui não interrompe nada).

## Organização do código

```
Program.cs                      host do serviço Windows + injeção de dependência
Worker.cs                       o ciclo: polling, decisão de fase, orquestração das 4 fases
Services/ApiService.cs          HTTP com a API central, validação de SHA-256
Services/DatabaseService.cs     Firebird: estado em SYS_ATUALIZACAO, injeção de BLOB no BEXE
Services/ExtractionService.cs   invoca o 7za.exe sobre os pacotes baixados
Services/ProcessService.cs      executa processos externos com timeout obrigatório
```

**Toda chamada a processo externo passa pelo `ProcessService` e exige timeout.**
Isso não é estilo, é segurança: a Fase 3 roda com o banco em `-shut force_0`
(bloqueado para todos os usuários), então um processo que trava sem timeout
deixaria o cliente inteiro parado até alguém perceber. Ver
[RISCOS-CONHECIDOS.md](RISCOS-CONHECIDOS.md#1-o-bscriptexe-real-pode-não-ter-modo-de-linha-de-comando).

O schema real do `BEXE.fdb` (tabela `EXECUTAVEIS`) foi confirmado por engenharia
reversa de um arquivo de produção. O do `JUNIOR.fdb` (`SYS_ATUALIZACAO`) **não** —
ver riscos conhecidos.
