-- Agrega cantidad por material dentro de cada caja
ALTER TABLE cajas_materiales
  ADD COLUMN IF NOT EXISTS cantidad numeric NOT NULL DEFAULT 1;
