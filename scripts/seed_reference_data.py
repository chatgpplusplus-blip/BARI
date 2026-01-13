#!/usr/bin/env python3
import argparse
import csv
import json
import os
from typing import Any, Dict, Iterable, List, Tuple

import requests
import psycopg


DEFAULT_HP_URL = "https://mhchem.github.io/hpstatements/clp/hpstatements-es-latest.json"

DEFAULT_PICTOGRAMS: List[Dict[str, str]] = [
    {
        "ghs_id": "GHS01",
        "descripcion": "Explosivo",
        "icon_url": "https://upload.wikimedia.org/wikipedia/commons/4/4a/GHS-pictogram-explos.svg",
        "detalle": "Explosivo",
    },
    {
        "ghs_id": "GHS02",
        "descripcion": "Inflamable",
        "icon_url": "https://upload.wikimedia.org/wikipedia/commons/6/6d/GHS-pictogram-flamme.svg",
        "detalle": "Inflamable",
    },
    {
        "ghs_id": "GHS03",
        "descripcion": "Comburente",
        "icon_url": "https://upload.wikimedia.org/wikipedia/commons/a/a6/GHS-pictogram-rondflam.svg",
        "detalle": "Oxidante",
    },
    {
        "ghs_id": "GHS04",
        "descripcion": "Gas a presión",
        "icon_url": "https://upload.wikimedia.org/wikipedia/commons/7/7b/GHS-pictogram-gas.svg",
        "detalle": "Gas comprimido",
    },
    {
        "ghs_id": "GHS05",
        "descripcion": "Corrosivo",
        "icon_url": "https://upload.wikimedia.org/wikipedia/commons/5/5f/GHS-pictogram-acid.svg",
        "detalle": "Corrosivo",
    },
    {
        "ghs_id": "GHS06",
        "descripcion": "Toxicidad aguda",
        "icon_url": "https://upload.wikimedia.org/wikipedia/commons/6/6b/GHS-pictogram-skull.svg",
        "detalle": "Tóxico",
    },
    {
        "ghs_id": "GHS07",
        "descripcion": "Irritante / nocivo",
        "icon_url": "https://upload.wikimedia.org/wikipedia/commons/3/3b/GHS-pictogram-exclam.svg",
        "detalle": "Irritante",
    },
    {
        "ghs_id": "GHS08",
        "descripcion": "Peligro grave para la salud",
        "icon_url": "https://upload.wikimedia.org/wikipedia/commons/2/2f/GHS-pictogram-silhouet.svg",
        "detalle": "Peligro salud",
    },
    {
        "ghs_id": "GHS09",
        "descripcion": "Peligro para el medio ambiente",
        "icon_url": "https://upload.wikimedia.org/wikipedia/commons/0/0a/GHS-pictogram-pollu.svg",
        "detalle": "Peligro ambiental",
    },
]

DEFAULT_CAS_SAMPLE: List[Dict[str, str]] = [
    {"cas_id": "7732-18-5", "nombre": "Agua", "categoria": "Inorgánico"},
    {"cas_id": "7647-01-0", "nombre": "Ácido clorhídrico", "categoria": "Ácido"},
    {"cas_id": "64-19-7", "nombre": "Ácido acético", "categoria": "Ácido"},
    {"cas_id": "56-81-5", "nombre": "Glicerina", "categoria": "Alcohol"},
    {"cas_id": "7664-93-9", "nombre": "Ácido sulfúrico", "categoria": "Ácido"},
    {"cas_id": "1310-73-2", "nombre": "Hidróxido de sodio", "categoria": "Base"},
]


def parse_hp_statements(payload: Dict[str, Any]) -> Iterable[Tuple[str, str]]:
    if isinstance(payload.get("hpstatements"), list):
        items = payload["hpstatements"]
    elif isinstance(payload.get("data"), list):
        items = payload["data"]
    elif isinstance(payload, list):
        items = payload
    else:
        items = []

    for item in items:
        code = item.get("code") or item.get("Code") or item.get("id") or ""
        statement = item.get("statement") or item.get("text") or item.get("desc") or item.get("description") or ""
        code = str(code).strip()
        statement = str(statement).strip()
        if not code or not statement:
            continue
        yield code, statement


def load_hp_codes(url: str) -> Tuple[List[Tuple[str, str]], List[Tuple[str, str]]]:
    response = requests.get(url, timeout=30)
    response.raise_for_status()
    payload = response.json()

    h_codes: List[Tuple[str, str]] = []
    p_codes: List[Tuple[str, str]] = []

    for code, statement in parse_hp_statements(payload):
        if code.upper().startswith("H"):
            h_codes.append((code, statement))
        elif code.upper().startswith("P"):
            p_codes.append((code, statement))

    return h_codes, p_codes


def load_pictograms(url: str | None) -> List[Dict[str, str]]:
    if not url:
        return DEFAULT_PICTOGRAMS
    response = requests.get(url, timeout=30)
    response.raise_for_status()
    payload = response.json()
    if isinstance(payload, dict) and isinstance(payload.get("pictograms"), list):
        return payload["pictograms"]
    if isinstance(payload, list):
        return payload
    return DEFAULT_PICTOGRAMS


def load_cas_catalog(url: str | None) -> List[Dict[str, str]]:
    if not url:
        return DEFAULT_CAS_SAMPLE
    response = requests.get(url, timeout=30)
    response.raise_for_status()
    text = response.text
    reader = csv.DictReader(text.splitlines())
    rows = []
    for row in reader:
        cas_id = row.get("cas_id") or row.get("cas") or row.get("CAS") or ""
        nombre = row.get("nombre") or row.get("name") or ""
        categoria = row.get("categoria") or row.get("category") or ""
        if cas_id and nombre:
            rows.append({"cas_id": cas_id.strip(), "nombre": nombre.strip(), "categoria": categoria.strip()})
    return rows


def upsert_h_codes(conn: psycopg.Connection, items: Iterable[Tuple[str, str]]) -> None:
    with conn.cursor() as cur:
        for code, desc in items:
            cur.execute(
                """
                INSERT INTO h_codes (h_id, descripcion)
                VALUES (%s, %s)
                ON CONFLICT (h_id) DO UPDATE SET descripcion = EXCLUDED.descripcion
                """,
                (code, desc),
            )


def upsert_p_codes(conn: psycopg.Connection, items: Iterable[Tuple[str, str]]) -> None:
    with conn.cursor() as cur:
        for code, desc in items:
            cur.execute(
                """
                INSERT INTO p_codes (p_id, descripcion)
                VALUES (%s, %s)
                ON CONFLICT (p_id) DO UPDATE SET descripcion = EXCLUDED.descripcion
                """,
                (code, desc),
            )


def upsert_pictograms(conn: psycopg.Connection, items: Iterable[Dict[str, str]]) -> None:
    with conn.cursor() as cur:
        for item in items:
            cur.execute(
                """
                INSERT INTO ghs_pictogramas (ghs_id, descripcion, icon_url, detalle)
                VALUES (%s, %s, %s, %s)
                ON CONFLICT (ghs_id) DO UPDATE
                SET descripcion = EXCLUDED.descripcion,
                    icon_url = EXCLUDED.icon_url,
                    detalle = EXCLUDED.detalle
                """,
                (item.get("ghs_id"), item.get("descripcion"), item.get("icon_url"), item.get("detalle")),
            )


def upsert_cas(conn: psycopg.Connection, items: Iterable[Dict[str, str]]) -> None:
    with conn.cursor() as cur:
        for item in items:
            cur.execute(
                """
                INSERT INTO cas_catalogo (cas_id, nombre, categoria)
                VALUES (%s, %s, %s)
                ON CONFLICT (cas_id) DO UPDATE
                SET nombre = EXCLUDED.nombre,
                    categoria = EXCLUDED.categoria
                """,
                (item.get("cas_id"), item.get("nombre"), item.get("categoria")),
            )


def main() -> None:
    parser = argparse.ArgumentParser(description="Carga referencias de seguridad en Supabase/Postgres.")
    parser.add_argument("--conn", help="Cadena de conexión Postgres", default=os.getenv("POSTGRES_CONNECTION_STRING"))
    parser.add_argument("--hp-url", default=DEFAULT_HP_URL, help="URL JSON con H/P statements (GHS)")
    parser.add_argument("--pictograms-url", default="", help="URL JSON con pictogramas GHS")
    parser.add_argument("--cas-url", default="", help="URL CSV con CAS (cas_id,nombre,categoria)")
    args = parser.parse_args()

    if not args.conn:
        raise SystemExit("Falta la cadena de conexión. Usa --conn o define POSTGRES_CONNECTION_STRING.")

    h_codes, p_codes = load_hp_codes(args.hp_url)
    pictograms = load_pictograms(args.pictograms_url or None)
    cas_rows = load_cas_catalog(args.cas_url or None)

    with psycopg.connect(args.conn) as conn:
        upsert_h_codes(conn, h_codes)
        upsert_p_codes(conn, p_codes)
        upsert_pictograms(conn, pictograms)
        upsert_cas(conn, cas_rows)
        conn.commit()

    print(f"H-codes cargados: {len(h_codes)}")
    print(f"P-codes cargados: {len(p_codes)}")
    print(f"Pictogramas cargados: {len(pictograms)}")
    print(f"CAS cargados: {len(cas_rows)}")


if __name__ == "__main__":
    main()
