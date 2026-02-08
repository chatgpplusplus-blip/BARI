ALTER TABLE sustancias
    ADD COLUMN IF NOT EXISTS palabra_advertencia text,
    ADD COLUMN IF NOT EXISTS concentracion text,
    ADD COLUMN IF NOT EXISTS pureza text;

ALTER TABLE contenedores
    ADD COLUMN IF NOT EXISTS proveedor_direccion text,
    ADD COLUMN IF NOT EXISTS proveedor_telefono_emergencia text;
