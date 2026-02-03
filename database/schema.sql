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
  alto_m numeric NOT NULL,
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
  UNIQUE (area_id, nombre_meson)
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
  UNIQUE (subcategoria_id, categoria_id) -- permite FK compuesta (subcategoria_id, categoria_id)
);

-- =========================================================
-- EQUIPOS
--  - modelos_equipo - EL EQUIPO ABSTRACTO DE UNA MARCA
--  - equipos: LA UNIDAD FISICA QUE SE TIENE, SE PUEDEN COMPRAR VARIOS DE UN MISMO MODELO 
-- =========================================================
CREATE TABLE IF NOT EXISTS modelos_equipo (
  modelo_id varchar PRIMARY KEY,
  marca_id varchar REFERENCES marcas(marca_id),
  categoria_id varchar REFERENCES categorias(categoria_id),
  nombre_modelo text NOT NULL,
  es_calibrable boolean NOT NULL,
  manual_url varchar REFERENCES documentos(documento_id),
  notas text,
  descripcion text,
  imagen_url text
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
-- SUSTANCIAS (ABSTRACTAS)
--  - ahora categoria_id + subcategorías M:N
-- =========================================================
CREATE TABLE IF NOT EXISTS sustancias (
  sustancia_id varchar PRIMARY KEY,
  nombre_comercial text,
  nombre_quimico text,
  cas varchar,
  forma_fisica text,
  sustancia_controlada boolean DEFAULT false,

  -- ✅ NUEVO: clasificación principal
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
  imagen_url text
);

-- =========================================================
-- MATERIALES (UNIFICADO)
--  - ahora categoria_id + subcategorías M:N
-- =========================================================
CREATE TABLE IF NOT EXISTS materiales (
  material_id varchar PRIMARY KEY,
  nombre text NOT NULL,

  -- Tipo físico
  tipo text NOT NULL CHECK (tipo IN ('VIDRIO','PLASTICO','MONTAJE','CONSUMIBLE')),

  -- ✅ NUEVO: clasificación principal
  categoria_id varchar REFERENCES categorias(categoria_id),

  marca_id varchar REFERENCES marcas(marca_id),
  estado_id varchar REFERENCES estados_activo(estado_id),

  -- Ubicación
  area_id varchar REFERENCES areas(area_id),
  meson_id varchar REFERENCES mesones(meson_id),
  nivel integer,
  posicion text,

  laboratorio_id integer NOT NULL REFERENCES laboratorios(laboratorio_id),

  -- Comunes
  observaciones text,
  descripcion text,
  imagen_url text,

  -- Especiales (dependen de tipo)
  capacidad_num numeric,
  unidad_id varchar REFERENCES unidades(unidad_id),
  cantidad numeric,

  CONSTRAINT materiales_vidrio_requiere_capacidad CHECK (
    tipo <> 'VIDRIO' OR (capacidad_num IS NOT NULL AND unidad_id IS NOT NULL)
  ),
  CONSTRAINT materiales_consumible_requiere_cantidad CHECK (
    tipo <> 'CONSUMIBLE' OR (cantidad IS NOT NULL)
  )
);

-- =========================================================
-- TABLAS INTERMEDIAS: subcategorías M:N
-- =========================================================
CREATE TABLE IF NOT EXISTS material_subcategorias (
  material_id varchar NOT NULL REFERENCES materiales(material_id) ON DELETE CASCADE,
  subcategoria_id varchar NOT NULL REFERENCES subcategorias(subcategoria_id) ON DELETE RESTRICT,
  PRIMARY KEY (material_id, subcategoria_id)
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
--  - mantiene subcategoria_id (1 sola)
-- =========================================================
CREATE TABLE IF NOT EXISTS instalaciones (
  instalacion_id varchar PRIMARY KEY,
  nombre text NOT NULL,

  subcategoria_id varchar REFERENCES subcategorias(subcategoria_id),

  laboratorio_id integer NOT NULL REFERENCES laboratorios(laboratorio_id),
  area_id varchar REFERENCES areas(area_id),

  canvas_id varchar REFERENCES canvas_lab(canvas_id),
  pos_x numeric,
  pos_y numeric,

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
-- DOCUMENTOS (mantiene subcategoria_id + FK compuesta)
-- =========================================================
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
      AND laboratorio_id IS NULL
      AND laboratorio_realizado_id IS NULL
      AND asignatura_id IS NULL
      AND experiencia_id IS NULL
      AND   modelo_equipo_id  IS NULL
      AND material_id IS NULL
      AND sustancia_id IS NULL
      AND cont_id IS NULL
    )
    OR
    (alcance='MARCA'
      AND marca_id IS NOT NULL
      AND laboratorio_id IS NULL
      AND laboratorio_realizado_id IS NULL
      AND asignatura_id IS NULL
      AND experiencia_id IS NULL
      AND   modelo_equipo_id  IS NULL
      AND material_id IS NULL
      AND sustancia_id IS NULL
      AND cont_id IS NULL
    )
    OR
    (alcance='LABORATORIO'
      AND laboratorio_id IS NOT NULL
      AND laboratorio_realizado_id IS NULL
      AND asignatura_id IS NULL
      AND experiencia_id IS NULL
      AND   modelo_equipo_id  IS NULL
      AND material_id IS NULL
      AND sustancia_id IS NULL
      AND cont_id IS NULL
    )
    OR
    (alcance='CLASE'
      AND laboratorio_id IS NULL
      AND laboratorio_realizado_id IS NOT NULL
      AND asignatura_id IS NULL
      AND experiencia_id IS NULL
      AND   modelo_equipo_id  IS NULL
      AND material_id IS NULL
      AND sustancia_id IS NULL
      AND cont_id IS NULL
    )
    OR
    (alcance='ASIGNATURA'
      AND laboratorio_id IS NULL
      AND laboratorio_realizado_id IS NULL
      AND asignatura_id IS NOT NULL
      AND experiencia_id IS NULL
      AND   modelo_equipo_id  IS NULL
      AND material_id IS NULL
      AND sustancia_id IS NULL
      AND cont_id IS NULL
    )
    OR
    (alcance='EXPERIENCIA'
      AND laboratorio_id IS NULL
      AND laboratorio_realizado_id IS NULL
      AND asignatura_id IS NULL
      AND experiencia_id IS NOT NULL
      AND   modelo_equipo_id  IS NULL
      AND material_id IS NULL
      AND sustancia_id IS NULL
      AND cont_id IS NULL
    )
    OR
    (alcance='EQUIPO'
      AND laboratorio_id IS NULL
      AND laboratorio_realizado_id IS NULL
      AND asignatura_id IS NULL
      AND experiencia_id IS NULL
      AND   modelo_equipo_id  IS NOT NULL
      AND material_id IS NULL
      AND sustancia_id IS NULL
      AND cont_id IS NULL
    )
    OR
    (alcance='MATERIAL'
      AND laboratorio_id IS NULL
      AND laboratorio_realizado_id IS NULL
      AND asignatura_id IS NULL
      AND experiencia_id IS NULL
      AND   modelo_equipo_id  IS NULL
      AND material_id IS NOT NULL
      AND sustancia_id IS NULL
      AND cont_id IS NULL
    )
    OR
    (alcance='SUSTANCIA'
      AND laboratorio_id IS NULL
      AND laboratorio_realizado_id IS NULL
      AND asignatura_id IS NULL
      AND experiencia_id IS NULL
      AND   modelo_equipo_id  IS NULL
      AND material_id IS NULL
      AND sustancia_id IS NOT NULL
      AND cont_id IS NULL
    )
    OR
    (alcance='CONTENEDOR'
      AND laboratorio_id IS NULL
      AND laboratorio_realizado_id IS NULL
      AND asignatura_id IS NULL
      AND experiencia_id IS NULL
      AND   modelo_equipo_id  IS NULL
      AND material_id IS NULL
      AND sustancia_id IS NULL
      AND cont_id IS NOT NULL
    )
  ),

  CONSTRAINT documentos_procedencia_marca_check CHECK (
    (procedencia = 'UPSA' AND marca_id = 'upsa')
    OR
    (procedencia = 'INSTITUCION INTERNACIONAL' AND marca_id IS NOT NULL AND marca_id <> 'upsa')
    OR
    (procedencia = 'INTERNO LABORATORIO' AND marca_id IS NULL)
  ),

  CONSTRAINT documentos_solo_marca_requiere_alcance_marca CHECK (
    NOT (
      marca_id IS NOT NULL
      AND laboratorio_id IS NULL
      AND laboratorio_realizado_id IS NULL
      AND asignatura_id IS NULL
      AND experiencia_id IS NULL
      AND   modelo_equipo_id  IS NULL
      AND material_id IS NULL
      AND sustancia_id IS NULL
      AND cont_id IS NULL
      AND alcance <> 'MARCA'
    )
  )
);

-- =========================================================
-- BLOQUES (interiores)
--  - corregido: exactamente uno entre instalacion o meson
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
  alto numeric NOT NULL,

  offset_x numeric NOT NULL DEFAULT 0,
  offset_y numeric NOT NULL DEFAULT 0,

  CONSTRAINT check_solamente_un_origen CHECK (
    num_nonnulls(instalacion_id, meson_id) = 1
  )
);

COMMIT;
-- =========================================================
-- 3) SEED: rellenar categorias y subcategorias
--    - Mantiene tus 6 categorías de inventario
--    - Reemplaza “Documentación” por 4 categorías de documentación
--    - Subcategorías: dejo “General” para inventario (mínimo)
--      y un set completo para documentación (sin NULL)
-- =========================================================
BEGIN;

-- ------------------------------
-- CATEGORÍAS (6 inventario + 4 documentación)
-- ------------------------------
INSERT INTO categorias (categoria_id, nombre) VALUES
  ('equipo-medicion',     'Equipos de Medicion'),
  ('equipo-operacion',     'Equipos de Operacion'),
  ('equipo-mixto',     'Equipos de Medicion y Operacion'),
  ('material-consumible',    'Material Consumible'),
  ('material-montaje',       'Material de Montaje/Soporte'),
  ('material-plastico',      'Material de Plástico'),
  ('material-vidrio',        'Material de Vidrio'),
  ('reactivo-quimico',       'Reactivo Químico'),

  ('doc-academica',          'Documentación Académica'),
  ('doc-upsa',               'Documentación Operación UPSA'),
  ('doc-gestion',            'Documentación Gestión y Formularios'),
  ('doc-normativa',          'Documentación Normativa y Referencia')
ON CONFLICT (categoria_id) DO NOTHING;

-- ------------------------------
-- SUBCATEGORÍAS (mínimo inventario: “General”)
-- ------------------------------
INSERT INTO subcategorias (subcategoria_id, categoria_id, nombre) VALUES
  ('equipo-general',             'equipo-mixto',  'General'),
  ('consumible-general',         'material-consumible', 'General'),
  ('montaje-general',            'material-montaje',    'General'),
  ('plastico-general',           'material-plastico',   'General'),
  ('vidrio-general',             'material-vidrio',     'General'),
  ('reactivo-general',           'reactivo-quimico',    'General')
ON CONFLICT (subcategoria_id) DO NOTHING;

-- ------------------------------
-- SUBCATEGORÍAS (documentación: completas, sin NULL)
-- ------------------------------
INSERT INTO subcategorias (subcategoria_id, categoria_id, nombre) VALUES
  -- Académica
  ('doc-acad-general',           'doc-academica', 'General'),
  ('doc-acad-guias',             'doc-academica', 'Guías de práctica'),
  ('doc-acad-planificacion',     'doc-academica', 'Planificación / Sílabo'),
  ('doc-acad-apoyo',             'doc-academica', 'Material de apoyo'),
  ('doc-acad-evaluacion',        'doc-academica', 'Evaluación / Rúbricas'),

  -- Operación UPSA
  ('doc-upsa-general',           'doc-upsa', 'General'),
  ('doc-upsa-manuales',          'doc-upsa', 'Manuales'),
  ('doc-upsa-procedimientos',    'doc-upsa', 'Procedimientos (SOP)'),
  ('doc-upsa-protocolos',        'doc-upsa', 'Protocolos internos'),
  ('doc-upsa-seguridad',         'doc-upsa', 'Seguridad / Emergencias'),
  ('doc-upsa-mantenimiento',     'doc-upsa', 'Mantenimiento'),

  -- Gestión y formularios
  ('doc-ges-general',            'doc-gestion', 'General'),
  ('doc-ges-formularios',        'doc-gestion', 'Formularios'),
  ('doc-ges-registros',          'doc-gestion', 'Registros'),
  ('doc-ges-checklists',         'doc-gestion', 'Checklists'),
  ('doc-ges-plantillas',         'doc-gestion', 'Plantillas'),
  ('doc-ges-informes',           'doc-gestion', 'Informes'),

  -- Normativa y referencia
  ('doc-norm-general',           'doc-normativa', 'General'),
  ('doc-norm-reglamentos',       'doc-normativa', 'Reglamentos / Políticas'),
  ('doc-norm-normas',            'doc-normativa', 'Normas externas'),
  ('doc-norm-fichas',            'doc-normativa', 'Fichas técnicas')
ON CONFLICT (subcategoria_id) DO NOTHING;

COMMIT;

BEGIN;

-- =========================================================
-- SUBCATEGORÍAS INVENTARIO (las nuevas)
--  - Mantiene tus "General"
--  - Agrega las subcategorías específicas
--  - Vidrio y Plástico: MISMAS subcategorías pero SEPARADAS
-- =========================================================
INSERT INTO subcategorias (subcategoria_id, categoria_id, nombre) VALUES
  -- -------------------------
  -- Material de Vidrio
  -- -------------------------
  ('vidrio-volumetrico',   'material-vidrio',   'Volumétrico'),
  ('vidrio-contencion',    'material-vidrio',   'De Contención'),
  ('vidrio-calentamiento', 'material-vidrio',   'De Calentamiento'),
  ('vidrio-separacion',    'material-vidrio',   'De Separación'),
  ('vidrio-manipulacion',  'material-vidrio',   'De Manipulación'),

  -- -------------------------
  -- Material de Plástico (mismas subcats que vidrio, pero separadas)
  -- -------------------------
  ('plastico-volumetrico',   'material-plastico', 'Volumétrico'),
  ('plastico-contencion',    'material-plastico', 'De Contención'),
  ('plastico-calentamiento', 'material-plastico', 'De Calentamiento'),
  ('plastico-separacion',    'material-plastico', 'De Separación'),
  ('plastico-manipulacion',  'material-plastico', 'De Manipulación'),

  -- -------------------------
  -- Equipo/Instrumento//Un modelo equipo mixto puede acumular propiedades de ambos
  -- -------------------------
  ('equipo-gravimetrico',                 'equipo-medicion', 'De Masa/Peso'),
  ('equipo-volumetrico',                 'equipo-medicion', 'De Volumen/Densidad'),
  ('equipo-electrometrico','equipo-medicion', 'Electrometrico/Electroquimico'),
  ('equipo-optico',               'equipo-medicion', 'De Optica'),
  ('equipo-temperatura',                  'equipo-medicion', 'De Temperatura'),
  ('equipo-termico',                  'equipo-operacion', 'De Enfriamiento/Calentamiento'),
  ('equipo-separacion',                  'equipo-operacion', 'De Separacion'),
  ('equipo-mezcla',                  'equipo-operacion', 'De Mezcla/Agitacion'),
  ('equipo-transferencia',                  'equipo-operacion', 'De Transferencia y Vacio'),



  -- -------------------------
  -- Material de Montaje/Soporte
  -- -------------------------
  ('montaje-estructuras-soporte',     'material-montaje', 'Estructuras de Soporte'),
  ('montaje-elementos-sujecion',      'material-montaje', 'Elementos de Sujeción'),
  ('montaje-accesorios-termico',      'material-montaje', 'Accesorios de montaje térmico'),
  ('montaje-soportes-contenedores',   'material-montaje', 'Soportes para Contenedores'),

  -- -------------------------
  -- Material Consumible
  -- -------------------------
  ('consumible-proteccion-bioseguridad','material-consumible', 'De Protección/Bioseguridad'),
  ('consumible-un-solo-uso',            'material-consumible', 'De un solo uso'),
  ('consumible-reutilizable',           'material-consumible', 'Reutilizable'),
  ('consumible-papeleria',              'material-consumible', 'Papelería de Laboratorio'),
  ('consumible-utensilios-auxiliares',  'material-consumible', 'Utensilios Auxiliares'),

  -- -------------------------
  -- Reactivo Químico (esto clasifica SUSTANCIAS)
  -- -------------------------
  ('reactivo-acido',              'reactivo-quimico', 'Ácido'),
  ('reactivo-base',               'reactivo-quimico', 'Base'),
  ('reactivo-sal',                'reactivo-quimico', 'Sal'),
  ('reactivo-solvente',           'reactivo-quimico', 'Solvente'),
  ('reactivo-reactivos-preparados','reactivo-quimico', 'Reactivos Preparados')
ON CONFLICT (subcategoria_id) DO NOTHING;


-- 1. Insertar las Categorías relacionadas con Instalaciones e Infraestructura
INSERT INTO categorias (categoria_id, nombre) VALUES
  ('ins-agua', 'Instalaciones Hidrosanitarias'),
  ('ins-clima', 'Instalaciones de Climatización'),
  ('ins-electrica', 'Instalaciones Electricas'),
  ('ins-gas', 'Instalaciones de Gases'),
  ('ins-mobiliario', 'Instalaciones de Mobiliarios'),
  ('ins-teleco', 'Instalaciones de Telecomunicaciones')
ON CONFLICT (categoria_id) DO UPDATE SET nombre = EXCLUDED.nombre;

-- 2. Insertar las Subcategorías correspondientes
INSERT INTO subcategorias (subcategoria_id, categoria_id, nombre) VALUES
  -- Agua
  ('duchas', 'ins-agua', 'Duchas'),
  ('lavaplatos', 'ins-agua', 'Lavaplatos'),
  
  -- Clima
  ('extraccion', 'ins-clima', 'Campanas Extractoras'),
  ('aires-acond', 'ins-clima', 'Aires Acondicionados'),
  
  -- Eléctrica
  ('tomacorrientes', 'ins-electrica', 'Tomacorrientes'),
  
  -- Gas
  ('gas-tanque', 'ins-gas', 'Gas de Tanque'),
  ('gas-red', 'ins-gas', 'Gas de Red'),
  
  -- Mobiliario
  ('ins-taburete', 'ins-mobiliario', 'Taburete'),
  ('mesas', 'ins-mobiliario', 'Mesas'),
  
  -- Telecomunicaciones
  ('telefonias', 'ins-teleco', 'Telefonias'),
  ('puntos-acceso', 'ins-teleco', 'Access Points de Internet'),
  ('Ethernet', 'ins-teleco', 'Ethernet (Voz y Datos)')
ON CONFLICT (subcategoria_id) DO UPDATE SET 
  nombre = EXCLUDED.nombre,
  categoria_id = EXCLUDED.categoria_id;

COMMIT;



BEGIN;

ALTER TABLE instalaciones
  ADD COLUMN IF NOT EXISTS altura_cm numeric;

ALTER TABLE modelos_equipo
  ADD COLUMN IF NOT EXISTS altura_cm numeric;

ALTER TABLE materiales
  ADD COLUMN IF NOT EXISTS altura_cm numeric;

ALTER TABLE contenedores
  ADD COLUMN IF NOT EXISTS altura_cm numeric;

COMMIT;

BEGIN;

-- 1) INSTALACIONES: eliminar columnas que ya no usarás
ALTER TABLE instalaciones
  DROP COLUMN IF EXISTS pos_x,
  DROP COLUMN IF EXISTS pos_y,
  DROP COLUMN IF EXISTS altura_cm;

-- 2) BLOQUES_INT: renombrar "alto" -> "largo"
ALTER TABLE bloques_int
  RENAME COLUMN alto TO largo;

-- 3) BLOQUES_INT: agregar columna "altura"
ALTER TABLE bloques_int
  ADD COLUMN IF NOT EXISTS altura numeric;

COMMIT;

BEGIN;

ALTER TABLE mesones
  ADD COLUMN IF NOT EXISTS imagen_url text;

ALTER TABLE canvas_lab
  DROP COLUMN alto_m,
  ADD COLUMN largo_m;
COMMIT;
