---
name: gerar-consulta-sql
description: "Use when: gerando consultas SQL para o ERP RM da TOTVS"
---

# Skill: Gerar Consulta SQL para ERP RM TOTVS

## Visão Geral

Esta skill guia a geração de consultas T-SQL corretas e otimizadas para o banco de dados do ERP RM da TOTVS, usando o MCP `totvs-rm-database-mcp-server` para descobrir tabelas, esquemas e relacionamentos.

---

## Fluxo de Trabalho

### Passo 1 — Entender o Pedido

Identifique claramente:
- **Qual dado** precisa ser retornado (colunas/entidades)
- **Qual módulo** do RM está envolvido (ver tabela de módulos abaixo)
- **Filtros** necessários (período, coligada, status, etc.)
- **Relacionamentos** esperados (quais entidades devem ser cruzadas)

Se o módulo não estiver claro, use `totvs_list_modules` para listar todos os módulos disponíveis.

#### Prefixos de Módulos do RM

| Prefixo | Módulo |
|---------|--------|
| `0`     | TOTVS Gestão de Custos |
| `A`     | TOTVS Automação de Ponto |
| `B`     | TOTVS Avaliação e Pesquisa |
| `C`     | TOTVS Gestão Contábil |
| `D`     | TOTVS Gestão Fiscal |
| `E`     | Ensino Básico |
| `F`     | TOTVS Gestão Financeira |
| `G`     | TOTVS Inteligência de Negócios |
| `H`     | TOTVS Aprovações e Atendimento |
| `I`     | TOTVS Gestão Patrimonial |
| `K`     | TOTVS Planejamento e Controle da Produção |
| `L`     | TOTVS Gestão Bibliotecária |
| `M`     | TOTVS Construção e Projetos |
| `N`     | TOTVS Manutenção |
| `O`     | TOTVS Saúde Hospitais e Clínicas |
| `P`     | TOTVS Folha de Pagamento |
| `R`     | TOTVS Segurança e Saúde Ocupacional |
| `S`     | TOTVS Educacional |
| `T`     | TOTVS Gestão de Estoque, Compras e Faturamento |
| `U`     | Ensino Superior |
| `V`     | TOTVS Gestão de Pessoas |
| `W`     | TOTVS Gestão de Conteúdos |
| `X`     | TOTVS Incorporação |
| `Y`     | TOTVS Controle de Acesso |

---

### Passo 2 — Descobrir as Tabelas

Use o MCP para localizar as tabelas relevantes:

```
# Quando souber palavras-chave:
totvs_search_tables(query="<termo de busca>")
```

> **Dica:** Busque pelo conceito de negócio (ex: "lancamento", "nota fiscal", "funcionario") e não pelo nome técnico da tabela.

---

### Passo 3 — Obter o Esquema e Regras das Tabelas

Para cada tabela identificada, obtenha seu schema completo:

```
totvs_get_table_schema(table="<NOME_TABELA>")
totvs_get_table_rules(table="<NOME_TABELA>")
```

O schema retorna:
- **Colunas** com tipos e descrições
- **Relacionamentos** — chaves estrangeiras de entrada e saída
- **Índices** — para identificar chaves primárias

> **Navegue pelos relacionamentos** para descobrir tabelas auxiliares necessárias (ex: tabelas de cadastro vinculadas via FK).
> **Sempre filtre pela coluna status** caso a tabela possua uma coluna de status ou situação, inclua filtro por status, consultando os possíveis valores com a ferramenta `totvs_get_table_rules` (não invente valores caso não tenha a informação documentada). Exemplo: `XVENDA.COD_SIT_VENDA = 40 -- 40 - Efetivada`, `FLAN.STATUSLAN = 0 -- 0 - Em Aberto`, `FBOLETO.STATUS = 0 -- 0 - Em Aberto`, `FCFO.ATIVO = 1 -- Apenas Clientes Ativos`, `PFUNC.CODSITUACAO = 'A' -- A - Ativo`, `SALUNO.CODTIPOCURSO = 1 -- 1 - Ensino Superior`, `SCONTRATO.CODSITUACAO = 'A' -- A - Ativo`, `MTAREFA.ATIVA = 1 -- 1 - Ativa`, `XALGCONTRATOLOC.SITUACAOCONTLOC = 0 -- 0 - Efetivado`, `XALGCONTRATOADM.SITUACAOCONTLOC = 0 -- 0 - Efetivado`, `XCONTRATO.COD_SIT_CONT = 40 -- 40 - Efetivado`, `MCNT.STATUS = 0 -- 0 - Em andamneto`, `MCNT.TIPO = 'R' -- R - A Receber`, `MCNT.CNTMATERIAL= 'S' -- S - Prestação de Serviços`, `OFCONTRATO.STATUS = 'A' -- A - Ativo`, `TCNT.CODTCN = '01' -- 01 - Venda de Produtos`, `TCNT.CODSTACNT = '01' -- 01 - Normal`, `SMATRICULA.CODSTATUS = 1 -- 1 - Matriculado`, `EMATRICPL.SITMAT = 1 -- 1 - Ativo`, `SMATRICPL.CODSTATUS = 1 -- 1 - Matriculado`, `UALUCURSO.STATUS = 1 -- 1 - Matriculado`, `TMOV.CODTMV = '1.1.04' -- 1.1.04 - Solicitação de Compra ( RM Solum )`, `DLAF.STATUSLF = 'N' -- N - Normal`
---

### Passo 4 — Montar a Consulta

Aplique **todas** as regras obrigatórias ao escrever o SQL:

#### ✅ Checklist de Regras

- [ ] **T-SQL exclusivamente** — nenhuma sintaxe de outros bancos
- [ ] **Nunca `SELECT *`** — liste sempre as colunas explicitamente
- [ ] **Qualifique todas as colunas** com alias da tabela (ex: `FLAN.CODCOLIGADA`)
- [ ] **`(NOLOCK)`** em todos os FROMs e JOINs de leitura
- [ ] **Filtre por `CODCOLIGADA`** em toda tabela que possua essa coluna
- [ ] **Prefira `INNER JOIN`** a subconsultas quando possível
- [ ] **Formate o SQL** com indentação e quebras de linha legíveis
- [ ] **Inclua comentários** explicando cada bloco lógico da query com `/*` e `*/`
- [ ] **Sempre valide a sintaxe do SQL** Sempre chame a ferramenta `totvs_validate_sql` do MCP `totvs-rm-database-mcp-server`
#### Template Base

```sql
/* =============================================
   Descrição: <objetivo da consulta>
   Tabelas:   <lista de tabelas principais>
   Filtros:   <filtros aplicados>
   ============================================= */
SELECT
    -- <Entidade principal>
    T1.COLUNA1,
    T1.COLUNA2,

    -- <Entidade relacionada>
    T2.COLUNA1,
    T2.COLUNA2

FROM TABELA_PRINCIPAL T1 (NOLOCK)

/* Relacionamento com tabela auxiliar */
INNER JOIN TABELA_AUXILIAR T2 (NOLOCK)
    ON  T2.CODCOLIGADA = T1.CODCOLIGADA
    AND T2.CHAVE       = T1.CHAVE_ESTRANGEIRA

WHERE
    /* Filtro obrigatório por coligada */
    T1.CODCOLIGADA = <CODCOLIGADA>

    /* Filtros de negócio */
    AND T1.COLUNA_FILTRO = <VALOR>

ORDER BY
    T1.COLUNA_ORDENACAO
```

---

### Passo 5 — Validar a Consulta

Antes de entregar, verifique cada item:

| # | Verificação | OK? |
|---|-------------|-----|
| 1 | Todas as colunas estão qualificadas com alias? | ☐ |
| 2 | Há `(NOLOCK)` em todos os FROM/JOIN? | ☐ |
| 3 | `CODCOLIGADA` está filtrado em todas as tabelas que o possuem? | ☐ |
| 4 | Não há `SELECT *`? | ☐ |
| 5 | JOINs incluem `CODCOLIGADA` quando a tabela a possui? | ☐ |
| 6 | SQL está formatado e com comentários? | ☐ |
| 7 | Colunas retornadas atendem ao pedido do usuário? | ☐ |
---

## Exemplos de Uso

### Exemplo 1 — Lançamentos Financeiros

```sql
/* =============================================
   Descrição: Lançamentos financeiros a receber
   Tabelas:   FLAN (Lançamentos), FCFO (Clientes/Fornecedores)
   Filtros:   Coligada, status em aberto
   ============================================= */
SELECT
    /* Dados do lançamento */
    FLAN.CODCOLIGADA,
    FLAN.IDLAN,
    FLAN.NUMERODOCUMENTO,
    FLAN.DATAEMISSAO,
    FLAN.DATAVENCIMENTO,
    FLAN.VALORORIGINAL,
    FLAN.VALORSALDO,
    FLAN.STATUSLAN,

    /* Dados do cliente/fornecedor */
    FCFO.NOME,
    FCFO.CGC AS CNPJ_CPF

FROM FLAN (NOLOCK)

/* Relacionamento com cadastro de clientes/fornecedores */
INNER JOIN FCFO (NOLOCK)
    ON  FCFO.CODCOLIGADA = FLAN.CODCOLIGADA
    AND FCFO.CODCFO      = FLAN.CODCFO

WHERE
    /* Filtro obrigatório por coligada */
    FLAN.CODCOLIGADA = 1

    /* Apenas títulos a receber em aberto */
    AND FLAN.STATUSLAN IN (0, 4) /* 0 - Em Aberto
                                    1 - Baixado
                                    2 - Cancelado
                                    3 - Baixado por Acordo
                                    4 - Baixado parcialmente
                                    5 - Borderô */

    AND FLAN.PAGREC = 1 /* 1 - Receber
                           2 - Pagar */

ORDER BY
    FLAN.DATAVENCIMENTO
```

### Exemplo 2 — Funcionários Ativos (RH)

```sql
/* =============================================
   Descrição: Funcionários ativos com cargo e lotação
   Tabelas:   PFUNC (Funcionários), PPESSOA (Pessoas), PFUNCAO (Funções)
   Filtros:   Coligada, situação ativa
   ============================================= */
SELECT
    /* Dados do funcionário */
    PFUNC.CODCOLIGADA,
    PFUNC.CHAPA,
    PFUNC.CODSECAO,
    PFUNC.DATAADMISSAO,

    /* Dados pessoais */
    PPESSOA.NOME,
    PPESSOA.CPF,

    /* Função/cargo */
    PFUNCAO.NOME AS CARGO

FROM PFUNC (NOLOCK)

/* Dados pessoais */
INNER JOIN PPESSOA (NOLOCK)
    ON PPESSOA.CODIGO = PFUNC.CODPESSOA

/* Cargo/função */
INNER JOIN PFUNCAO (NOLOCK)
    ON  PFUNCAO.CODCOLIGADA = PFUNC.CODCOLIGADA
    AND PFUNCAO.CODIGO      = PFUNC.CODFUNCAO

WHERE
    /* Filtro obrigatório por coligada */
    PFUNC.CODCOLIGADA = 1

    /* Apenas funcionários ativos */
    AND PFUNC.CODSITUACAO = 'A' /* A - Ativo
                                   I - Inativo
                                   F - Férias
                                   D - Demitido
                                   U - Outros */
ORDER BY
    PPESSOA.NOME
```

---

## Dicas de Troubleshooting

| Problema | Solução |
|----------|---------|
| Não sei o nome da tabela | Use `totvs_search_tables` com termos de negócio |
| JOIN retorna duplicatas | Verifique se incluiu `CODCOLIGADA` na condição do JOIN |
| Coluna não encontrada | Consulte `totvs_get_table_schema` para confirmar o nome exato |
| Query lenta | Use `totvs_get_db_index` para identificar colunas indexadas |
| Relacionamento não claro | Leia a seção "Relacionamentos" no schema retornado pelo MCP |
