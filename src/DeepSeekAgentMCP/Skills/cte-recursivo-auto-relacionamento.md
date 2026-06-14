# Skill: Consulta com CTE Recursivo (Auto-Relacionamento)

## Quando usar

Use esta skill quando o usuário precisar de uma consulta SQL com CTE recursivo para tabelas que possuem auto-relacionamento (uma coluna que referencia a própria tabela, como `IDPAI → ID`).

## Estrutura esperada da tabela

- Uma coluna de ID (PK) identifica cada registro
- Uma coluna de ID pai (FK) referencia o ID do registro pai
- Quando `ID_PAI` é NULL ou 0, o registro é raiz (nível 0)

## Template SQL

```sql
WITH CTE_RECURSIVO AS (
    -- Âncora: registros raiz ou ponto de partida específico
    SELECT
        [colunas],
        0 AS NIVEL
    FROM [Tabela] (NOLOCK)
    WHERE [filtro_inicial]

    UNION ALL

    -- Recursiva: filhos de cada registro encontrado
    SELECT
        t.[colunas],
        c.NIVEL + 1 AS NIVEL
    FROM [Tabela] t (NOLOCK)
    INNER JOIN CTE_RECURSIVO c
        ON t.[ID_PAI] = c.[ID]
       AND [outros_filtros_chave_composta]
)
SELECT [colunas], NIVEL
FROM CTE_RECURSIVO
ORDER BY [codigo_hierarquico];
```

## Exemplo prático (MTAREFA)

Tabela com auto-relacionamento: `IDPAI` → `IDTRF` (ambos na mesma tabela `MTAREFA`).

```sql
WITH CTE_RECURSIVO AS (
    -- Âncora: tarefa raiz específica
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
      AND CODTRF = '004.01.01'

    UNION ALL

    -- Recursiva: filhos de cada tarefa
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
ORDER BY CODTRF;
```

## Regras importantes

1. **Sempre use `UNION ALL`** entre âncora e parte recursiva
2. **Coluna `NIVEL`** obrigatória para rastrear profundidade (incrementar com `+ 1`)
3. **Chaves compostas**: quando a PK for composta, inclua TODAS as colunas no JOIN recursivo
4. **`(NOLOCK)`** recomendado para consultas de leitura em produção
5. **Limite de profundidade** (opcional): `WHERE c.NIVEL < 20` para evitar recursão infinita
6. **ORDER BY** por código hierárquico para preservar a estrutura de árvore
