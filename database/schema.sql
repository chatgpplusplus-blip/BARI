BEGIN;

-- =========================================================
-- SEGURIDAD QUÍMICA
-- =========================================================
CREATE TABLE IF NOT EXISTS h_codes (
  h_id varchar PRIMARY KEY,
  descripcion text NOT NULL UNIQUE,
  grupo text,
  nota text
);

CREATE TABLE IF NOT EXISTS p_codes (
  p_id varchar PRIMARY KEY,
  descripcion text NOT NULL UNIQUE,
  grupo text,
  nota text
);

CREATE TABLE IF NOT EXISTS ghs_pictogramas (
  ghs_id varchar PRIMARY KEY,
  descripcion text NOT NULL,
  icon_url text,
  detalle text
);

-- =========================================================
-- LABORATORIOS Y ESPACIO FÍSICO
-- =========================================================
CREATE TABLE IF NOT EXISTS laboratorios (
  laboratorio_id integer PRIMARY KEY,
  nombre text NOT NULL UNIQUE,
  tiene_ghs boolean NOT NULL DEFAULT false
);

CREATE TABLE IF NOT EXISTS plantas (
  planta_id text PRIMARY KEY,
  nombre text NOT NULL
);

CREATE TABLE IF NOT EXISTS unidades (
  unidad_id varchar PRIMARY KEY,
  nombre text NOT NULL,
  simbolo text NOT NULL,
  UNIQUE (nombre, simbolo)
);

CREATE TABLE IF NOT EXISTS canvas_lab (
  canvas_id varchar PRIMARY KEY,
  nombre text NOT NULL,
  ancho_m numeric NOT NULL,
  largo_m numeric NOT NULL, -- Renombrado de alto_m
  margen_m numeric NOT NULL,
  anotaciones text,
  laboratorio_id integer REFERENCES laboratorios(laboratorio_id)
);

CREATE TABLE IF NOT EXISTS areas (
  area_id varchar PRIMARY KEY,
  nombre_areas text NOT NULL UNIQUE,
  altura_m numeric,
  area_total_m2 numeric,
  anotaciones_del_area text,
  descripcion text,
  laboratorio_id integer NOT NULL REFERENCES laboratorios(laboratorio_id),
  planta_id text REFERENCES plantas(planta_id),
  canvas_id varchar REFERENCES canvas_lab(canvas_id)
);

CREATE TABLE IF NOT EXISTS poligonos (
  poly_id varchar PRIMARY KEY,
  canvas_id varchar NOT NULL REFERENCES canvas_lab(canvas_id),
  area_id varchar REFERENCES areas(area_id),
  etiqueta text,
  color_hex text,
  z_order integer NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS poligonos_puntos (
  punto_id bigserial PRIMARY KEY,
  poly_id varchar NOT NULL REFERENCES poligonos(poly_id) ON DELETE CASCADE,
  orden integer NOT NULL,
  x_m numeric NOT NULL,
  y_m numeric NOT NULL,
  UNIQUE (poly_id, orden)
);

CREATE TABLE IF NOT EXISTS puertas (
  puerta_id varchar PRIMARY KEY,
  canvas_id varchar NOT NULL REFERENCES canvas_lab(canvas_id),
  area_a varchar REFERENCES areas(area_id),
  area_b varchar REFERENCES areas(area_id),
  x1_m numeric NOT NULL,
  y1_m numeric NOT NULL,
  x2_m numeric NOT NULL,
  y2_m numeric NOT NULL,
  grosor_m numeric NOT NULL,
  color_hex text NOT NULL,
  nota text
);

CREATE TABLE IF NOT EXISTS ventanas (
  ventana_id varchar PRIMARY KEY,
  canvas_id varchar NOT NULL REFERENCES canvas_lab(canvas_id),
  area_a varchar REFERENCES areas(area_id),
  area_b varchar REFERENCES areas(area_id),
  x1_m numeric NOT NULL,
  y1_m numeric NOT NULL,
  x2_m numeric NOT NULL,
  y2_m numeric NOT NULL,
  grosor_m numeric NOT NULL,
  color_hex text NOT NULL,
  nota text
);

CREATE TABLE IF NOT EXISTS mesones (
  meson_id varchar PRIMARY KEY,
  area_id varchar NOT NULL REFERENCES areas(area_id),
  nombre_meson text NOT NULL,
  niveles_totales integer,
  laboratorio_id integer NOT NULL REFERENCES laboratorios(laboratorio_id),
  imagen_url text, -- Agregado
  UNIQUE (area_id, nombre_meson)
);

CREATE TABLE IF NOT EXISTS cajas (
  caja_id varchar PRIMARY KEY,
  meson_id varchar NOT NULL REFERENCES mesones(meson_id) ON DELETE CASCADE,
  nivel integer NOT NULL,
  dimensiones text
);

-- =========================================================
-- CATÁLOGOS BÁSICOS
-- =========================================================
CREATE TABLE IF NOT EXISTS marcas (
  marca_id varchar PRIMARY KEY,
  nombre text NOT NULL UNIQUE,
  imagen_url text
);

CREATE TABLE IF NOT EXISTS estados_activo (
  estado_id varchar PRIMARY KEY,
  nombre text NOT NULL
);

CREATE TABLE IF NOT EXISTS condiciones (
  condicion_id varchar PRIMARY KEY,
  nombre text NOT NULL UNIQUE
);

-- =========================================================
-- CATEGORÍAS Y SUBCATEGORÍAS
-- =========================================================
CREATE TABLE IF NOT EXISTS categorias (
  categoria_id varchar PRIMARY KEY,
  nombre text NOT NULL UNIQUE
);

CREATE TABLE IF NOT EXISTS subcategorias (
  subcategoria_id varchar PRIMARY KEY,
  categoria_id varchar NOT NULL REFERENCES categorias(categoria_id),
  nombre text NOT NULL,
  UNIQUE (categoria_id, nombre),
  UNIQUE (subcategoria_id, categoria_id)
);

-- =========================================================
-- EQUIPOS
-- =========================================================
CREATE TABLE IF NOT EXISTS documentos (
  -- Se crea primero para poder ser referenciada en modelos_equipo,
  -- PERO modelos_equipo referencia documentos, y documentos referencia modelos_equipo.
  -- PostgreSQL permite esto si se crea la tabla sin la FK circular primero y se añade despues,
  -- O si simplemente creamos la tabla documentos al final y usamos ALTER.
  -- Para mantener "un solo script limpio", pondremos documentos al final y modelos_equipo aquí.
  -- *Nota: Si modelos_equipo tiene FK a documentos, documentos debe existir antes.*
  -- *Solución en script limpio: Crear documentos (simplificada) -> Crear modelos -> Alter documentos.*
  -- Sin embargo, para no complicar el script consolidado, asumiremos que manual_url
  -- es solo texto o que la tabla documentos se crea antes sin las FKs circulares.
  -- *Estrategia aplicada abajo:* Definimos documentos después, pero modelos_equipo la referencia.
  -- Esto fallaría en orden secuencial estricto.
  -- CORRECCIÓN: Dejaré modelos_equipo con manual_url como TEXT temporalmente o asumo 
  -- que el usuario ejecutará esto en bloque.
  -- Para que funcione 100% en copy-paste, crearé una tabla 'dummy' o quitaré la referencia directa
  -- en CREATE y la pondré al final. 
  -- *Decisión:* Dejar la referencia. Si falla, mover 'documentos' antes.
  -- Como 'documentos' tiene FKs a TODO, es mejor crearla al final.
  -- La FK de modelos_equipo(manual_url) -> documentos(documento_id) requiere documentos creada.
  documento_id varchar PRIMARY KEY
  -- (Definición completa más abajo, esto es solo un placeholder mental si reordenáramos).
);
-- *Nota: PostgreSQL no permite forward reference en CREATE TABLE.
--  Voy a poner la tabla documentos AL FINAL, y añadiré la FK de modelos_equipo
--  vía ALTER al final del script para garantizar integridad sin errores.*

CREATE TABLE IF NOT EXISTS modelos_equipo (
  modelo_id varchar PRIMARY KEY,
  marca_id varchar REFERENCES marcas(marca_id),
  categoria_id varchar REFERENCES categorias(categoria_id),
  nombre_modelo text NOT NULL,
  es_calibrable boolean NOT NULL,
  manual_url varchar, -- Se añadirá FK al final
  notas text,
  descripcion text,
  imagen_url text,
  altura_cm numeric -- Agregado
);

-- =========================================================
-- PERSONAL Y HORARIOS
-- =========================================================
CREATE TABLE IF NOT EXISTS docentes (
  docente_id integer PRIMARY KEY GENERATED ALWAYS AS IDENTITY,
  usuario_id integer NOT NULL,
  nombre text NOT NULL,
  imagen_url text,
  activo boolean NOT NULL DEFAULT true,
  created_at timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at timestamp
);

CREATE TABLE IF NOT EXISTS becarios (
  becario_id integer PRIMARY KEY GENERATED ALWAYS AS IDENTITY,
  usuario_id integer NOT NULL,
  nombre text NOT NULL,
  imagen_url text,
  activo boolean NOT NULL DEFAULT true,
  created_at timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at timestamp
);

CREATE TABLE IF NOT EXISTS horarios_docentes (
  horario_docente_id bigserial PRIMARY KEY,
  docente_id integer NOT NULL REFERENCES docentes(docente_id),
  laboratorio_id integer NOT NULL REFERENCES laboratorios(laboratorio_id),
  fecha date NOT NULL,
  hora_inicio time NOT NULL,
  hora_fin time NOT NULL,
  created_at timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS horarios_becarios (
  horario_becario_id bigserial PRIMARY KEY,
  becario_id integer NOT NULL REFERENCES becarios(becario_id),
  laboratorio_id integer NOT NULL REFERENCES laboratorios(laboratorio_id),
  fecha date NOT NULL,
  hora_inicio time NOT NULL,
  hora_fin time NOT NULL,
  created_at timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS horarios_clases_becario (
  horario_clase_id bigserial PRIMARY KEY,
  becario_id integer NOT NULL REFERENCES becarios(becario_id),
  fecha date NOT NULL,
  hora_inicio time NOT NULL,
  hora_fin time NOT NULL,
  created_at timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS equipos (
  equipo_id varchar PRIMARY KEY,
  nombre text NOT NULL,
  modelo_id varchar REFERENCES modelos_equipo(modelo_id),
  serie text,
  estado_id varchar REFERENCES estados_activo(estado_id),
  area_id varchar REFERENCES areas(area_id),
  meson_id varchar REFERENCES mesones(meson_id),
  nivel integer,
  posicion text,
  canvas_id varchar REFERENCES canvas_lab(canvas_id),
  categoria_id varchar REFERENCES categorias(categoria_id),
  fecha_compra date,
  garantia_hasta date,
  requiere_calibracion boolean NOT NULL,
  laboratorio_id integer NOT NULL REFERENCES laboratorios(laboratorio_id),
  observaciones text,
  imagen_url text
);

CREATE TABLE IF NOT EXISTS calibraciones (
  cal_id varchar PRIMARY KEY,
  equipo_id varchar NOT NULL REFERENCES equipos(equipo_id),
  fecha date NOT NULL,
  resultado text,
  proxima_fecha date,
  certificado_url text,
  proveedor text,
  costo numeric,
  notas text
);

-- =========================================================
-- SUSTANCIAS
-- =========================================================
CREATE TABLE IF NOT EXISTS sustancias (
  sustancia_id varchar PRIMARY KEY,
  nombre_comercial text,
  nombre_quimico text,
  cas varchar,
  forma_fisica text,
  palabra_advertencia text,
  concentracion text,
  pureza text,
  sustancia_controlada boolean DEFAULT false,
  categoria_id varchar REFERENCES categorias(categoria_id),
  laboratorio_id integer NOT NULL REFERENCES laboratorios(laboratorio_id),
  observaciones text,
  descripcion text,
  imagen_url text,
  CONSTRAINT sustancias_nombre_minimo CHECK (
    nombre_comercial IS NOT NULL OR nombre_quimico IS NOT NULL
  )
);

CREATE TABLE IF NOT EXISTS sustancias_h (
  sustancia_id varchar NOT NULL REFERENCES sustancias(sustancia_id),
  h_id varchar NOT NULL REFERENCES h_codes(h_id),
  PRIMARY KEY (sustancia_id, h_id)
);

CREATE TABLE IF NOT EXISTS sustancias_p (
  sustancia_id varchar NOT NULL REFERENCES sustancias(sustancia_id),
  p_id varchar NOT NULL REFERENCES p_codes(p_id),
  PRIMARY KEY (sustancia_id, p_id)
);

CREATE TABLE IF NOT EXISTS sustancias_pictogramas (
  sustancia_id varchar NOT NULL REFERENCES sustancias(sustancia_id),
  ghs_id varchar NOT NULL REFERENCES ghs_pictogramas(ghs_id),
  PRIMARY KEY (sustancia_id, ghs_id)
);

CREATE TABLE IF NOT EXISTS rombo (
  rombo_id varchar PRIMARY KEY,
  seccion text NOT NULL CHECK (seccion IN ('SALUD', 'INFLAMABILIDAD', 'REACTIVIDAD', 'ESPECIAL')),
  numero integer,
  codigo text,
  significado text NOT NULL,
  lugar text NOT NULL,
  CONSTRAINT rombo_valor_valido CHECK (
    (seccion = 'ESPECIAL' AND codigo IS NOT NULL AND numero IS NULL)
    OR
    (seccion <> 'ESPECIAL' AND codigo IS NULL AND numero BETWEEN 0 AND 4)
  ),
  CONSTRAINT rombo_unique_valor UNIQUE (seccion, numero, codigo)
);

CREATE TABLE IF NOT EXISTS sustancia_rombo (
  sustancia_id varchar NOT NULL REFERENCES sustancias(sustancia_id) ON DELETE CASCADE,
  seccion text NOT NULL CHECK (seccion IN ('SALUD', 'INFLAMABILIDAD', 'REACTIVIDAD', 'ESPECIAL')),
  rombo_id varchar NOT NULL REFERENCES rombo(rombo_id),
  PRIMARY KEY (sustancia_id, seccion)
);

INSERT INTO rombo (rombo_id, seccion, numero, codigo, significado, lugar)
VALUES
  ('SALUD-0', 'SALUD', 0, NULL, 'Sin riesgo significativo para la salud.', 'Azul (salud)'),
  ('SALUD-1', 'SALUD', 1, NULL, 'Irritación o lesión menor.', 'Azul (salud)'),
  ('SALUD-2', 'SALUD', 2, NULL, 'Lesión temporal o residual.', 'Azul (salud)'),
  ('SALUD-3', 'SALUD', 3, NULL, 'Lesión seria o permanente.', 'Azul (salud)'),
  ('SALUD-4', 'SALUD', 4, NULL, 'Riesgo extremo o mortal.', 'Azul (salud)'),
  ('INFLAMABILIDAD-0', 'INFLAMABILIDAD', 0, NULL, 'No arde.', 'Rojo (inflamabilidad)'),
  ('INFLAMABILIDAD-1', 'INFLAMABILIDAD', 1, NULL, 'Requiere precalentamiento para arder.', 'Rojo (inflamabilidad)'),
  ('INFLAMABILIDAD-2', 'INFLAMABILIDAD', 2, NULL, 'Debe calentarse moderadamente para arder.', 'Rojo (inflamabilidad)'),
  ('INFLAMABILIDAD-3', 'INFLAMABILIDAD', 3, NULL, 'Puede encenderse a temperatura ambiente.', 'Rojo (inflamabilidad)'),
  ('INFLAMABILIDAD-4', 'INFLAMABILIDAD', 4, NULL, 'Extremadamente inflamable.', 'Rojo (inflamabilidad)'),
  ('REACTIVIDAD-0', 'REACTIVIDAD', 0, NULL, 'Estable.', 'Amarillo (reactividad)'),
  ('REACTIVIDAD-1', 'REACTIVIDAD', 1, NULL, 'Inestable si se calienta.', 'Amarillo (reactividad)'),
  ('REACTIVIDAD-2', 'REACTIVIDAD', 2, NULL, 'Cambio violento posible.', 'Amarillo (reactividad)'),
  ('REACTIVIDAD-3', 'REACTIVIDAD', 3, NULL, 'Puede detonar con fuerte iniciación o calor.', 'Amarillo (reactividad)'),
  ('REACTIVIDAD-4', 'REACTIVIDAD', 4, NULL, 'Detona fácilmente.', 'Amarillo (reactividad)'),
  ('ESPECIAL-OX', 'ESPECIAL', NULL, 'OX', 'Oxidante.', 'Blanco (especial)'),
  ('ESPECIAL-W', 'ESPECIAL', NULL, 'W', 'Reacciona con agua (no usar agua).', 'Blanco (especial)'),
  ('ESPECIAL-SA', 'ESPECIAL', NULL, 'SA', 'Gas asfixiante simple.', 'Blanco (especial)'),
  ('ESPECIAL-COR', 'ESPECIAL', NULL, 'COR', 'Corrosivo.', 'Blanco (especial)'),
  ('ESPECIAL-ACID', 'ESPECIAL', NULL, 'ACID', 'Ácido.', 'Blanco (especial)'),
  ('ESPECIAL-ALK', 'ESPECIAL', NULL, 'ALK', 'Alcalino.', 'Blanco (especial)'),
  ('ESPECIAL-BIO', 'ESPECIAL', NULL, 'BIO', 'Riesgo biológico.', 'Blanco (especial)'),
  ('ESPECIAL-RAD', 'ESPECIAL', NULL, 'RAD', 'Radiactivo.', 'Blanco (especial)'),
  ('ESPECIAL-CRYO', 'ESPECIAL', NULL, 'CRYO', 'Criogénico.', 'Blanco (especial)')
ON CONFLICT DO NOTHING;

-- =========================================================
-- CONTENEDORES
-- =========================================================
CREATE TABLE IF NOT EXISTS contenedores (
  cont_id varchar PRIMARY KEY,
  sustancia_id varchar NOT NULL REFERENCES sustancias(sustancia_id),
  marca_id varchar REFERENCES marcas(marca_id),
  densidad_g_ml numeric,
  fecha_compra date,
  proveedor text,
  proveedor_direccion text,
  proveedor_telefono_emergencia text,
  fecha_recepcion date,
  fecha_vencimiento date,
  masa_envase_vacio_g numeric,
  masa_tapa_g numeric,
  color_envase text,
  unidad_id varchar REFERENCES unidades(unidad_id),
  cantidad_reactivo_nominal numeric,
  cantidad_reactivo_actual numeric,
  fecha_apertura date,
  condicion_id varchar REFERENCES condiciones(condicion_id),
  observaciones text,
  area_id varchar REFERENCES areas(area_id),
  meson_id varchar REFERENCES mesones(meson_id),
  nivel integer,
  posicion text,
  laboratorio_id integer NOT NULL REFERENCES laboratorios(laboratorio_id),
  qr text,
  imagen_url text,
  altura_cm numeric -- Agregado
);

-- =========================================================
-- MATERIALES
-- =========================================================
CREATE TABLE IF NOT EXISTS materiales (
  material_id varchar PRIMARY KEY,
  nombre text NOT NULL,
  tipo text NOT NULL CHECK (tipo IN ('VIDRIO','PLASTICO','MONTAJE','CONSUMIBLE')),
  categoria_id varchar REFERENCES categorias(categoria_id),
  marca_id varchar REFERENCES marcas(marca_id),
  estado_id varchar REFERENCES estados_activo(estado_id),
  area_id varchar REFERENCES areas(area_id),
  posicion text,
  laboratorio_id integer NOT NULL REFERENCES laboratorios(laboratorio_id),
  observaciones text,
  descripcion text,
  imagen_url text,
  capacidad_num numeric,
  unidad_id varchar REFERENCES unidades(unidad_id),
  cantidad numeric,
  altura_cm numeric, -- Agregado
  CONSTRAINT materiales_vidrio_requiere_capacidad CHECK (
    tipo <> 'VIDRIO' OR (capacidad_num IS NOT NULL AND unidad_id IS NOT NULL)
  ),
  CONSTRAINT materiales_consumible_requiere_cantidad CHECK (
    tipo <> 'CONSUMIBLE' OR (cantidad IS NOT NULL)
  )
);

-- =========================================================
-- TABLAS INTERMEDIAS
-- =========================================================
CREATE TABLE IF NOT EXISTS material_subcategorias (
  material_id varchar NOT NULL REFERENCES materiales(material_id) ON DELETE CASCADE,
  subcategoria_id varchar NOT NULL REFERENCES subcategorias(subcategoria_id) ON DELETE RESTRICT,
  PRIMARY KEY (material_id, subcategoria_id)
);

CREATE TABLE IF NOT EXISTS materiales_mesones_niveles (
  material_id varchar NOT NULL REFERENCES materiales(material_id) ON DELETE CASCADE,
  meson_id varchar NOT NULL REFERENCES mesones(meson_id) ON DELETE CASCADE,
  nivel integer NOT NULL,
  PRIMARY KEY (material_id, meson_id, nivel)
);

CREATE TABLE IF NOT EXISTS cajas_materiales (
  caja_id varchar NOT NULL REFERENCES cajas(caja_id) ON DELETE CASCADE,
  material_id varchar NOT NULL REFERENCES materiales(material_id) ON DELETE CASCADE,
  cantidad numeric NOT NULL DEFAULT 1,
  PRIMARY KEY (caja_id, material_id)
);

CREATE TABLE IF NOT EXISTS modelo_equipo_subcategorias (
  modelo_id varchar NOT NULL REFERENCES modelos_equipo(modelo_id) ON DELETE CASCADE,
  subcategoria_id varchar NOT NULL REFERENCES subcategorias(subcategoria_id) ON DELETE RESTRICT,
  PRIMARY KEY (modelo_id, subcategoria_id)
);

CREATE TABLE IF NOT EXISTS sustancia_subcategorias (
  sustancia_id varchar NOT NULL REFERENCES sustancias(sustancia_id) ON DELETE CASCADE,
  subcategoria_id varchar NOT NULL REFERENCES subcategorias(subcategoria_id) ON DELETE RESTRICT,
  PRIMARY KEY (sustancia_id, subcategoria_id)
);

CREATE INDEX IF NOT EXISTS idx_material_subcats_subcat  ON material_subcategorias(subcategoria_id);
CREATE INDEX IF NOT EXISTS idx_modelo_equipo_subcats_subcat ON modelo_equipo_subcategorias (subcategoria_id);
CREATE INDEX IF NOT EXISTS idx_sustancia_subcats_subcat ON sustancia_subcategorias(subcategoria_id);

-- =========================================================
-- ASIGNATURAS Y EXPERIENCIAS
-- =========================================================
CREATE TABLE IF NOT EXISTS asignaturas (
  asignatura_id varchar PRIMARY KEY,
  nombre text NOT NULL,
  codigo_clase text NOT NULL UNIQUE,
  descripcion text,
  laboratorio_id integer REFERENCES laboratorios(laboratorio_id),
  descripcion_detalle text
);

CREATE TABLE IF NOT EXISTS laboratorio_realizado (
  laboratorio_realizado_id varchar PRIMARY KEY,
  laboratorio_id integer NOT NULL REFERENCES laboratorios(laboratorio_id),
  asignatura_id varchar REFERENCES asignaturas(asignatura_id),
  nombre text NOT NULL,
  descripcion text
);

CREATE TABLE IF NOT EXISTS experiencias_clases (
  experiencia_id varchar PRIMARY KEY,
  asignatura_id varchar NOT NULL REFERENCES asignaturas(asignatura_id),
  nombre text NOT NULL,
  materiales_usados text,
  equipos_usados text,
  procedimientos text,
  tiempo_estimado_min integer,
  observaciones text,
  precauciones text,
  orden integer,
  laboratorio_id integer REFERENCES laboratorios(laboratorio_id),
  descripcion_detalle text,
  laboratorio_realizado_id varchar REFERENCES laboratorio_realizado(laboratorio_realizado_id)
);

CREATE TABLE IF NOT EXISTS experiencia_equipos (
  experiencia_id varchar NOT NULL REFERENCES experiencias_clases(experiencia_id),
  modelo_equipo_id varchar NOT NULL REFERENCES modelos_equipo(modelo_id),
  PRIMARY KEY (experiencia_id, modelo_equipo_id)
);

CREATE TABLE IF NOT EXISTS experiencia_materiales (
  experiencia_id varchar NOT NULL REFERENCES experiencias_clases(experiencia_id),
  material_id varchar NOT NULL REFERENCES materiales(material_id),
  PRIMARY KEY (experiencia_id, material_id)
);

CREATE TABLE IF NOT EXISTS experiencia_sustancias (
  experiencia_id varchar NOT NULL REFERENCES experiencias_clases(experiencia_id),
  sustancia_id varchar NOT NULL REFERENCES sustancias(sustancia_id),
  PRIMARY KEY (experiencia_id, sustancia_id)
);

-- =========================================================
-- INSTALACIONES (Infraestructura Fija)
-- =========================================================
CREATE TABLE IF NOT EXISTS instalaciones (
  instalacion_id varchar PRIMARY KEY,
  nombre text NOT NULL,
  subcategoria_id varchar REFERENCES subcategorias(subcategoria_id),
  laboratorio_id integer NOT NULL REFERENCES laboratorios(laboratorio_id),
  area_id varchar REFERENCES areas(area_id),
  canvas_id varchar REFERENCES canvas_lab(canvas_id),
  -- Eliminados pos_x, pos_y, altura_cm (según secuencia final)
  estado_id varchar REFERENCES estados_activo(estado_id),
  fecha_instalacion date,
  fecha_ultima_revision date,
  fecha_proxima_revision date,
  proveedor_servicio text,
  observaciones text,
  descripcion text,
  imagen_url text
);

-- =========================================================
-- DOCUMENTOS
-- =========================================================
DROP TABLE IF EXISTS documentos CASCADE; -- Por seguridad si el dummy de arriba existiera

CREATE TABLE IF NOT EXISTS documentos (
  documento_id varchar PRIMARY KEY,
  titulo text NOT NULL,
  categoria_id varchar NOT NULL REFERENCES categorias(categoria_id),
  subcategoria_id varchar NOT NULL,
  CONSTRAINT fk_documentos_subcat_cat
    FOREIGN KEY (subcategoria_id, categoria_id)
    REFERENCES subcategorias(subcategoria_id, categoria_id),

  descripcion_detalle text,
  url text,
  archivo_local text,
  notas text,
  imagen_url text,
  marca_id varchar REFERENCES marcas(marca_id),
  procedencia text NOT NULL DEFAULT 'INTERNO LABORATORIO'
    CHECK (procedencia IN ('INTERNO LABORATORIO','INSTITUCION INTERNACIONAL','UPSA')),
  laboratorio_contexto_id integer REFERENCES laboratorios(laboratorio_id),
  alcance text NOT NULL DEFAULT 'GENERAL'
    CHECK (alcance IN (
      'GENERAL','MARCA','LABORATORIO','CLASE','ASIGNATURA','EXPERIENCIA',
      'EQUIPO','MATERIAL','SUSTANCIA','CONTENEDOR'
    )),

  laboratorio_id integer REFERENCES laboratorios(laboratorio_id),
  laboratorio_realizado_id varchar REFERENCES laboratorio_realizado(laboratorio_realizado_id),
  asignatura_id varchar REFERENCES asignaturas(asignatura_id),
  experiencia_id varchar REFERENCES experiencias_clases(experiencia_id),
  modelo_equipo_id varchar REFERENCES modelos_equipo(modelo_id),
  material_id varchar REFERENCES materiales(material_id),
  sustancia_id varchar REFERENCES sustancias(sustancia_id),
  cont_id varchar REFERENCES contenedores(cont_id),

  CONSTRAINT documentos_tiene_archivo_o_url CHECK (
    url IS NOT NULL OR archivo_local IS NOT NULL
  ),

  CONSTRAINT documentos_alcance_xor CHECK (
    (alcance='GENERAL'
      AND laboratorio_id IS NULL AND laboratorio_realizado_id IS NULL
      AND asignatura_id IS NULL AND experiencia_id IS NULL AND  modelo_equipo_id  IS NULL
      AND material_id IS NULL AND sustancia_id IS NULL AND cont_id IS NULL
    ) OR
    (alcance='MARCA'
      AND marca_id IS NOT NULL
      AND laboratorio_id IS NULL AND laboratorio_realizado_id IS NULL
      AND asignatura_id IS NULL AND experiencia_id IS NULL AND  modelo_equipo_id  IS NULL
      AND material_id IS NULL AND sustancia_id IS NULL AND cont_id IS NULL
    ) OR
    (alcance='LABORATORIO'
      AND laboratorio_id IS NOT NULL
      AND laboratorio_realizado_id IS NULL AND asignatura_id IS NULL AND experiencia_id IS NULL
      AND  modelo_equipo_id  IS NULL AND material_id IS NULL AND sustancia_id IS NULL AND cont_id IS NULL
    ) OR
    (alcance='CLASE'
      AND laboratorio_id IS NULL AND laboratorio_realizado_id IS NOT NULL
      AND asignatura_id IS NULL AND experiencia_id IS NULL AND  modelo_equipo_id  IS NULL
      AND material_id IS NULL AND sustancia_id IS NULL AND cont_id IS NULL
    ) OR
    (alcance='ASIGNATURA'
      AND laboratorio_id IS NULL AND laboratorio_realizado_id IS NULL
      AND asignatura_id IS NOT NULL AND experiencia_id IS NULL AND  modelo_equipo_id  IS NULL
      AND material_id IS NULL AND sustancia_id IS NULL AND cont_id IS NULL
    ) OR
    (alcance='EXPERIENCIA'
      AND laboratorio_id IS NULL AND laboratorio_realizado_id IS NULL
      AND asignatura_id IS NULL AND experiencia_id IS NOT NULL AND  modelo_equipo_id  IS NULL
      AND material_id IS NULL AND sustancia_id IS NULL AND cont_id IS NULL
    ) OR
    (alcance='EQUIPO'
      AND laboratorio_id IS NULL AND laboratorio_realizado_id IS NULL
      AND asignatura_id IS NULL AND experiencia_id IS NULL AND  modelo_equipo_id  IS NOT NULL
      AND material_id IS NULL AND sustancia_id IS NULL AND cont_id IS NULL
    ) OR
    (alcance='MATERIAL'
      AND laboratorio_id IS NULL AND laboratorio_realizado_id IS NULL
      AND asignatura_id IS NULL AND experiencia_id IS NULL AND  modelo_equipo_id  IS NULL
      AND material_id IS NOT NULL AND sustancia_id IS NULL AND cont_id IS NULL
    ) OR
    (alcance='SUSTANCIA'
      AND laboratorio_id IS NULL AND laboratorio_realizado_id IS NULL
      AND asignatura_id IS NULL AND experiencia_id IS NULL AND  modelo_equipo_id  IS NULL
      AND material_id IS NULL AND sustancia_id IS NOT NULL AND cont_id IS NULL
    ) OR
    (alcance='CONTENEDOR'
      AND laboratorio_id IS NULL AND laboratorio_realizado_id IS NULL
      AND asignatura_id IS NULL AND experiencia_id IS NULL AND  modelo_equipo_id  IS NULL
      AND material_id IS NULL AND sustancia_id IS NULL AND cont_id IS NOT NULL
    )
  ),

  CONSTRAINT documentos_procedencia_marca_check CHECK (
    (procedencia = 'UPSA' AND marca_id = 'upsa') OR
    (procedencia = 'INSTITUCION INTERNACIONAL' AND marca_id IS NOT NULL AND marca_id <> 'upsa') OR
    (procedencia = 'INTERNO LABORATORIO' AND marca_id IS NULL)
  ),

  CONSTRAINT documentos_solo_marca_requiere_alcance_marca CHECK (
    NOT (
      marca_id IS NOT NULL
      AND laboratorio_id IS NULL AND laboratorio_realizado_id IS NULL
      AND asignatura_id IS NULL AND experiencia_id IS NULL AND  modelo_equipo_id  IS NULL
      AND material_id IS NULL AND sustancia_id IS NULL AND cont_id IS NULL
      AND alcance <> 'MARCA'
    )
  )
);

-- Cierre de la relación circular entre modelos_equipo y documentos
ALTER TABLE modelos_equipo 
  ADD CONSTRAINT fk_modelos_manual 
  FOREIGN KEY (manual_url) REFERENCES documentos(documento_id);

-- =========================================================
-- BLOQUES (interiores)
-- =========================================================
CREATE TABLE IF NOT EXISTS bloques_int (
  bloque_id varchar PRIMARY KEY,
  canvas_id varchar NOT NULL REFERENCES canvas_lab(canvas_id),
  instalacion_id varchar UNIQUE REFERENCES instalaciones(instalacion_id),
  meson_id varchar UNIQUE REFERENCES mesones(meson_id),
  etiqueta text,
  color_hex text,
  z_order integer NOT NULL DEFAULT 0,
  pos_x numeric NOT NULL,
  pos_y numeric NOT NULL,
  ancho numeric NOT NULL,
  largo numeric NOT NULL, -- Renombrado de alto
  altura numeric,         -- Agregado
  offset_x numeric NOT NULL DEFAULT 0,
  offset_y numeric NOT NULL DEFAULT 0,

  CONSTRAINT check_solamente_un_origen CHECK (
    num_nonnulls(instalacion_id, meson_id) = 1
  )
);

COMMIT;
