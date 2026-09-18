# ADR-0005 — Uma instância do agente por cliente, não por sistema

**Situação:** Aceita

## Contexto

Um cliente pode ter vários produtos do ERP instalados (B_Vendas, B_NFe,
BImportaXML...). A modelagem intuitiva seria instalar um agente por produto,
cada um cuidando do seu.

Investigando a instalação real, porém: um cliente tem **um** `JUNIOR.fdb` e
**um** `BEXE.fdb`, servindo todos os produtos — confirmado abrindo um `BEXE.fdb`
de produção com três produtos na mesma tabela `EXECUTAVEIS`.

## Decisão

**Uma instância do agente por cliente**, que conhece a lista de sistemas
(`SISTEMAS`) e processa cada um separadamente dentro do mesmo ciclo.

Quais sistemas aquele cliente realmente tem é descoberto **em tempo de
execução**: o agente verifica se o executável declarado em `NomeExeEsperado`
existe na pasta do cliente. Só quem passa nesse filtro é atualizado.

## Consequências

**Ganhos**
- Não há duas instâncias disputando a mesma linha de `SYS_ATUALIZACAO`, nem
  dois `gfix -shut` concorrentes no mesmo banco. Instâncias separadas por
  sistema brigariam exatamente por esses recursos.
- A Fase 3 (banco parado) acontece no máximo uma vez por ciclo, não uma vez por
  produto — menos interrupção para o cliente.
- **Publicar uma versão nova de um produto não afeta clientes que não o têm.**
  Sem o filtro por executável presente, todo cliente tentaria baixar toda
  versão de todo produto.
- Instalar é uma pasta, um serviço, um `.ini`.

**Custos aceitos**
- O `Worker` fica mais complexo: precisa iterar sistemas, manter estado por
  sistema e decidir a ordem das fases considerando todos.
- Um sistema com erro pode atrasar os outros do mesmo ciclo.
- O filtro depende de o nome do executável estar certo no `.ini`. Um nome
  errado faz o sistema ser silenciosamente ignorado — e por isso o nome do
  `.exe` é declarado explicitamente em `SistemaConfigurado`, e não inferido do
  nome do sistema: os dois não batem sempre (`B_NFe` → `B_NFE.exe`).

## Alternativas consideradas

- **Um serviço do Windows por sistema** — descartado: concorrência destrutiva
  sobre um banco compartilhado (ver ganhos), além de multiplicar o trabalho de
  instalação e de configuração por cliente.
- **Um agente só, atualizando tudo que estiver no pacote sem filtrar** —
  descartado: faria todo cliente baixar e aplicar produto que não tem
  instalado.
