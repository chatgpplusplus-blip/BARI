-- =========================================================
-- TABLA INTERMEDIA: MATERIALES - MESONES - NIVELES
-- =========================================================
CREATE TABLE IF NOT EXISTS materiales_mesones_niveles (
  material_id varchar NOT NULL REFERENCES materiales(material_id) ON DELETE CASCADE,
  meson_id varchar NOT NULL REFERENCES mesones(meson_id) ON DELETE CASCADE,
  nivel integer NOT NULL,
  PRIMARY KEY (material_id, meson_id, nivel)
);

-- =========================================================
-- CAJAS Y RELACION CAJA - MATERIALES
-- =========================================================
CREATE TABLE IF NOT EXISTS cajas (
  caja_id varchar PRIMARY KEY,
  meson_id varchar NOT NULL REFERENCES mesones(meson_id) ON DELETE CASCADE,
  nivel integer NOT NULL,
  dimensiones text
);

CREATE TABLE IF NOT EXISTS cajas_materiales (
  caja_id varchar NOT NULL REFERENCES cajas(caja_id) ON DELETE CASCADE,
  material_id varchar NOT NULL REFERENCES materiales(material_id) ON DELETE CASCADE,
  PRIMARY KEY (caja_id, material_id)
);

-- =========================================================
-- LEVANTAMIENTO (ALTURA DESDE SUELO) PARA INSTALACIONES
-- =========================================================
CREATE TABLE IF NOT EXISTS levantamientos_instalaciones (
  instalacion_id varchar PRIMARY KEY REFERENCES instalaciones(instalacion_id) ON DELETE CASCADE,
  altura_desde_suelo_cm numeric NOT NULL
);

-- =========================================================
-- MIGRACION DE MATERIALES (meson_id, nivel) A TABLA INTERMEDIA
-- =========================================================
INSERT INTO materiales_mesones_niveles (material_id, meson_id, nivel)
SELECT material_id, meson_id, nivel
FROM materiales
WHERE meson_id IS NOT NULL
  AND nivel IS NOT NULL;

-- =========================================================
-- ELIMINAR COLUMNAS meson_id y nivel EN materiales
-- =========================================================
ALTER TABLE materiales
  DROP COLUMN meson_id,
  DROP COLUMN nivel;
