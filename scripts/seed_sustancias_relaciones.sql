BEGIN;

-- Sustancias -> H codes (ejemplos basados en los datos de muestra)
INSERT INTO sustancias_h (sustancia_id, h_id)
SELECT s.sustancia_id, h.h_id
FROM (VALUES
    ('sus-1', 'EUH014'),
    ('sus-2', 'EUH018')
) AS v(sustancia_id, h_id)
JOIN sustancias s ON s.sustancia_id = v.sustancia_id
JOIN h_codes h ON h.h_id = v.h_id
ON CONFLICT DO NOTHING;

-- Sustancias -> P codes (ejemplos basados en los datos de muestra)
INSERT INTO sustancias_p (sustancia_id, p_id)
SELECT s.sustancia_id, p.p_id
FROM (VALUES
    ('sus-1', 'P101'),
    ('sus-2', 'P102')
) AS v(sustancia_id, p_id)
JOIN sustancias s ON s.sustancia_id = v.sustancia_id
JOIN p_codes p ON p.p_id = v.p_id
ON CONFLICT DO NOTHING;

-- Sustancias -> Pictogramas (ejemplos basados en los datos de muestra)
INSERT INTO sustancias_pictogramas (sustancia_id, ghs_id)
SELECT s.sustancia_id, g.ghs_id
FROM (VALUES
    ('sus-1', 'GHS05'),
    ('sus-2', 'GHS06'),
    ('sus-3', 'GHS02')
) AS v(sustancia_id, ghs_id)
JOIN sustancias s ON s.sustancia_id = v.sustancia_id
JOIN ghs_pictogramas g ON g.ghs_id = v.ghs_id
ON CONFLICT DO NOTHING;

COMMIT;
