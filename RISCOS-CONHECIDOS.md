# Riscos conhecidos e pendências

Levantado por leitura linha a linha do código e por engenharia reversa dos
binários e da base de produção reais (`BScript.exe`, `BEXE.FDB`, `BScript.Ini` e
1026 scripts DDL). Última revisão: **28/08/2026**.

**Nenhum cliente real deve receber este agente antes do item 1 ser resolvido.**

---

## 🔴 Bloqueador externo

### 1. O `BScript.exe` real pode não ter modo de linha de comando

O agente chama `BScript.exe /silent /db="..."` ([Worker.cs](Worker.cs), Fase 3)
como se fosse uma ferramenta de console. A extração de strings do binário real
(5 MB, compilado em 2019) **não encontrou nenhuma evidência disso**: nenhuma
ocorrência de `/silent`, `-silent`, `FindCmdLineSwitch` ou `-db=`. O que apareceu
foi um formulário Delphi/FireDAC completo — campos de Servidor, Banco e pasta de
Scripts, um `TFileOpenDialog` com multisseleção manual, um `RadioGroup` de
filtro, e dois botões: *"Selecionar Scripts Pasta"* e *"Inserir No Banco Como
Executado"* (este último, uma confirmação manual feita depois de olhar o
resultado na tela).

Rodando dentro de um serviço Windows (sessão 0, sem desktop interativo), o mais
provável é uma janela que ninguém consegue ver nem clicar.

**Mitigação já no código:** `ProcessService` exige timeout em toda chamada e mata
o processo se estourar (10 min para o BScript). Isso evita o pior caso — o
servidor do cliente travado para sempre com o banco em `-shut force_0` — mas
**não** faz a automação funcionar.

**Ação:** confirmar com quem mantém o `BScript.exe` se existe uma versão com modo
headless de verdade. Se não existir, ele precisa ganhar um, ou ser substituído por
um executor de scripts escrito para isso.

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

## 🟠 Alto

### 4. O backoff quase nunca entra em ação
Falha na Fase 3 → estado `ERRO` → o ciclo seguinte cai no ramo `CONCLUIDO || ERRO`,
refaz a Fase 1 com sucesso e zera `_falhasConsecutivas`. O contador zera antes de
crescer, então o backoff protege contra falhas de *download*, não contra falhas da
*atualização* — o inverso do pretendido. Cada `ERRO` ainda dispara um re-download
completo e devolve o cliente a `PENDENTE`, exigindo nova autorização do usuário.

### 5. "Sucesso" sem ter atualizado nada
Se a extração não produzir nenhum `.exe`, `InjetarNovosBinarios` itera sobre uma
lista vazia, commita, e o fluxo grava `CONCLUIDO` + reporta `SUCESSO` à API. Falta
validar que o pacote continha o que deveria.

### 6. A reversão de versão depende de um `.txt` que pode sumir
`versao_anterior.txt` fica em `AppDomain.CurrentDomain.BaseDirectory` — a pasta de
instalação do serviço. Se sumir (reinstalação, limpeza) ou não puder ser escrita
(`Program Files` sem permissão para a conta do serviço), o `catch` não reverte
`VERSAO_NOVA` — e volta em silêncio o bug original: o próximo polling enxerga o
cliente como "em dia" e para de tentar para sempre.

**Correção sugerida:** uma coluna própria (`VERSAO_ATUAL` separada de
`VERSAO_NOVA`) em `SYS_ATUALIZACAO`. O estado do banco não deveria depender de um
arquivo solto fora dele.

---

## 🟡 Médio

### 7. API quebrada conta como ciclo saudável
`ApiService.CheckForUpdates` engole *toda* exceção — 401 por token errado, JSON
inválido, DNS morto — e devolve `null`. O Worker interpreta como "sem atualização",
zera o contador de falhas e segue batendo a cada 10s indefinidamente. Sem log
local, sem backoff, sem sinal no painel: um cliente com token errado fica invisível.

### 8. O `gbak` pós-atualização é indisponibilidade jogada fora
Até 15 minutos com o banco em `-shut force_0`, e o `JUNIOR_POS.fbk` é apagado
poucas linhas depois, no `Directory.Delete(_tempPath, true)`. Nada lê esse arquivo.
Ou ele é copiado para fora do `TEMP_PATH`, ou deveria sair do caminho crítico.

### 9. Downloads com timeout de 100 s
O `HttpClient` usa o padrão do .NET e `DownloadPackages` não recebe
`CancellationToken`. Um pacote grande em link de cliente estoura, e o serviço não
consegue parar durante um download.

### 10. A senha do Firebird aparece na linha de comando
`ArgumentList` resolveu o parsing (senha com espaço/aspas), não a exposição:
qualquer processo local lê a linha de comando do `gfix`/`gbak` via Gerenciador de
Tarefas ou WMI. As variáveis `ISC_USER`/`ISC_PASSWORD` em
`ProcessStartInfo.Environment` evitariam isso.

---

## ⚪ Pendências de ambiente (não corrigíveis só com código)

- **Schema do `JUNIOR.fdb` não confirmado.** Nenhuma cópia estava disponível para
  inspeção; os nomes de `SYS_ATUALIZACAO` (`STATUS`, `VERSAO_NOVA`, `MENSAGEM_LOG`)
  são assumidos. O `BEXE.fdb` **foi** confirmado (tabela `EXECUTAVEIS`).
- **Fase 2 (Delphi) não existe.** Não há nenhum `.pas`/`.dpr`. Ler `PENDENTE`,
  perguntar ao usuário e gravar `AUTORIZADO` ainda precisa ser escrito no ERP.
- **Rollback nunca testado.** O `gbak -c -replace_database` está no código mas nunca
  foi exercitado contra uma cópia real do Firebird 2.5 do cliente.
- **Idempotência dos scripts.** Os 1026 scripts históricos (2011 → 08/2026) não têm
  proteção contra reexecução (`IF NOT EXISTS` ou equivalente), e o `BScript.exe`
  registra "executado" por clique manual. Rodar a pasta inteira num cliente que não
  parta de banco zerado gera erros de "objeto já existe" — que a política atual
  (qualquer exit code ≠ 0 aborta e reverte) trata como falha crítica. É uma decisão
  de processo: como versionar quais scripts cada release exige.
- **Convenção de `NOMEPRODUTO`.** A injeção usa o nome do arquivo sem extensão como
  padrão; confirmar se é isso que os terminais esperam.
- **Testes de integração.** Não existem. Antes do primeiro cliente, vale um ciclo
  completo contra cópias descartáveis dos dois bancos.
