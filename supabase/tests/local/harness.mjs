// Local check of the migrations without Docker. Starts a throwaway Postgres, fakes the few Supabase pieces the migrations rely on
// (auth schema, roles, storage tables, pg_cron), and applies the migrations in order.
import EmbeddedPostgres from 'embedded-postgres';
import pg from 'pg';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import net from 'node:net';
import { fileURLToPath } from 'node:url';

const MIGRATIONS = process.env.MIGRATIONS ?? path.join(path.dirname(fileURLToPath(import.meta.url)), '../../migrations');
async function unusedPort() {
  const socket = net.createServer();
  await new Promise((resolve, reject) => { socket.once('error', reject); socket.listen(0, '127.0.0.1', resolve); });
  const port = socket.address().port;
  await new Promise(resolve => socket.close(resolve));
  return port;
}
const DIR = fs.mkdtempSync(path.join(os.tmpdir(), 'gym-pg-data-'));

const stubs = `
create role anon nologin; create role authenticated nologin; create role service_role nologin bypassrls;
create schema auth; create schema extensions; create schema storage; create schema cron;
create publication supabase_realtime;
grant usage on schema auth, extensions, storage, public to anon, authenticated, service_role;
create table auth.users (
  id uuid primary key default gen_random_uuid(),
  email text unique, raw_user_meta_data jsonb not null default '{}', raw_app_meta_data jsonb not null default '{}'
);
create function auth.uid() returns uuid language sql stable as
  $$ select nullif(current_setting('request.jwt.claim.sub', true), '')::uuid $$;
create function auth.jwt() returns jsonb language sql stable as
  $$ select coalesce(nullif(current_setting('request.jwt.claims', true), ''), '{}')::jsonb $$;
create table storage.buckets (id text primary key, name text, public bool, file_size_limit bigint, allowed_mime_types text[]);
create table storage.objects (id uuid primary key default gen_random_uuid(), bucket_id text, name text);
alter table storage.objects enable row level security;
create function storage.foldername(name text) returns text[] language sql immutable as $$ select string_to_array(name, '/') $$;
create function cron.schedule(job text, sched text, cmd text) returns bigint language sql as $$ select 1::bigint $$;
`;

export async function start({ beforeMigration } = {}) {
  const PORT = await unusedPort();
  // PG18's Windows I/O workers can stall initdb; these disposable tests use synchronous I/O.
  const flags = process.platform === 'win32' ? ['-c', 'io_method=sync'] : [];
  const server = new EmbeddedPostgres({ databaseDir: DIR, user: 'postgres', password: 'pw', port: PORT, persistent: false,
    initdbFlags: flags, postgresFlags: flags });
  await server.initialise();
  await server.start();
  const client = new pg.Client({ host: 'localhost', port: PORT, user: 'postgres', password: 'pw', database: 'postgres' });
  await client.connect();
  await client.query(stubs);
  const files = fs.readdirSync(MIGRATIONS).filter(f => f.endsWith('.sql')).sort();
  for (const f of files) {
    // pg_cron is a Supabase extension; the stub schema above stands in for it
    const sql = fs.readFileSync(path.join(MIGRATIONS, f), 'utf8').replace(/create extension if not exists pg_cron[^;]*;/i, '');
    try { await beforeMigration?.(client, f, sql); await client.query(sql); console.log('ok  ', f); }
    catch (e) { console.log('FAIL', f, '\n    ', e.message, e.position ? `(pos ${e.position})` : '', e.where ?? ''); await client.end(); await server.stop(); throw e; }
  }
  return { server, client };
}

if (process.argv[1].endsWith('harness.mjs')) {
  const { server, client } = await start();
  await client.end(); await server.stop();
}
