# Skill: Consulta com CTE Recursivo (Auto-Relacionamento)

## Quando usar

Use esta skill quando o usuário precisar de uma consulta SQL com CTE recursivo para tabelas que possuem auto-relacionamento (uma coluna que referencia a própria tabela, como `IDPAI → ID`).

## Estrutura esperada da tabela

- Uma coluna de ID (PK) identifica cada registro
- Uma coluna de ID pai (FK) referencia o ID do registro pai

## Template SQL

```sql
WITH CTE_RECURSIVO AS (
    -- Âncora: a própria tarefa raiz (opcional, ou pode começar só pelos filhos)
    SELECT
        CODCOLIGADA,
        IDPRJ,
        [ID],
        [IDPAI],
        0 AS NIVEL
    FROM [TABELA] (NOLOCK)
    WHERE CODCOLIGADA = 1
      AND IDPRJ = 2
      AND [ID] = 126

    UNION ALL

    -- Passo recursivo: busca os filhos de cada tarefa encontrada
    SELECT
        t.CODCOLIGADA,
        t.IDPRJ,
        t.[ID],
        t.[IDPAI],
        c.NIVEL + 1 AS NIVEL
    FROM [TABELA] t (NOLOCK)
    INNER JOIN CTE_RECURSIVO c
        ON t.CODCOLIGADA = c.CODCOLIGADA
       AND t.IDPRJ = c.IDPRJ
       AND t.[IDPAI] = c.[ID]
       AND t.[IDPAI] <> t.[ID]
    WHERE
       c.NIVEL < 10 -- Nível máximo
)
SELECT
    CODCOLIGADA,
    IDPRJ,
    [ID],
    [IDPAI],
    NIVEL
FROM CTE_RECURSIVO
OPTION (MAXRECURSION 10)    
```

## Exemplo prático (MTAREFA)

Tabela com auto-relacionamento: `IDPAI` → `IDTRF` (ambos na mesma tabela `MTAREFA`).
Pedido: Listar a hierarquia de todas as tarefas da tarefa 126 do projeto 2 e coligada 1.

```sql
WITH CTE_RECURSIVO AS (
    -- Âncora: a própria tarefa raiz (opcional, ou pode começar só pelos filhos)
    SELECT
        CODCOLIGADA,
        IDPRJ,
        IDTRF,
        CODTRF,
        NOME,
        IDPAI,
        0 AS NIVEL
    FROM MTAREFA (NOLOCK)
    WHERE CODCOLIGADA = 1
      AND IDPRJ = 2
      AND IDTRF = 126    

    UNION ALL

    -- Passo recursivo: busca os filhos de cada tarefa encontrada
    SELECT
        t.CODCOLIGADA,
        t.IDPRJ,
        t.IDTRF,
        t.CODTRF,
        t.NOME,
        t.IDPAI,
        c.NIVEL + 1 AS NIVEL
    FROM MTAREFA t (NOLOCK)
    INNER JOIN CTE_RECURSIVO c
        ON t.CODCOLIGADA = c.CODCOLIGADA
       AND t.IDPRJ = c.IDPRJ
       AND t.IDPAI = c.IDTRF
       AND t.IDPAI <> t.IDTRF
    WHERE
        c.NIVEL < 10 -- Nível máximo
)
SELECT
    CODCOLIGADA,
    IDPRJ,
    IDTRF,
    CODTRF,
    NOME,
    IDPAI,
    NIVEL
FROM CTE_RECURSIVO
ORDER BY 
    CODTRF
OPTION (MAXRECURSION 10)
```

## Regras importantes

1. **Sempre use `UNION ALL`** entre âncora e parte recursiva
2. **Coluna `NIVEL`** obrigatória para rastrear profundidade (incrementar com `+ 1`)
3. **Chaves compostas**: quando a PK for composta, inclua TODAS as colunas no JOIN recursivo
4. **`(NOLOCK)`** recomendado para consultas de leitura em produção
5. **Limite de profundidade** (opcional): `WHERE c.NIVEL < 10` para evitar recursão infinita
6. **ORDER BY** por código hierárquico para preservar a estrutura de árvore
