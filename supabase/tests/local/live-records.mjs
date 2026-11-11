// Phase 7 (F4, 2026-11-11): payments, check-ins and membership terms are published for live updates. Realtime only sends a change to
// someone whose row rules let them read that row, so the rules must stay on and a member must still see only their own rows.
import { start } from './harness.mjs';
import assert from 'node:assert/strict';

const { server, client: db } = await start();
let passed = 0, failed = 0, serial = 0;
async function as(uid, sql, args = []) {
  await db.query('begin');
  try {
    await db.query('set local role authenticated');
    await db.query(`select set_config('request.jwt.claim.sub', $1, true)`, [uid]);
    const r = await db.query(sql, args);
    await db.query('commit');
    return r.rows;
  } catch (e) { await db.query('rollback'); throw e; }
}
async function user(role = 'member') {
  return (await db.query(`insert into auth.users(email,raw_user_meta_data,raw_app_meta_data)
    values($1,'{"full_name":"Live Person"}',$2) returning id`, [`live-${++serial}@test.io`, JSON.stringify({ role })])).rows[0].id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}

const tables = ['payments', 'gym_check_ins', 'membership_terms'];

await test('payments, check-ins and membership terms are in the live publication, with chat still there', async () => {
  const published = (await db.query(`select tablename from pg_publication_tables where pubname = 'supabase_realtime' and schemaname = 'public'`)).rows.map(r => r.tablename);
  for (const t of [...tables, 'messages', 'support_messages']) assert.ok(published.includes(t), `${t} is not published`);
});

await test('row rules stay on for every published table', async () => {
  const rows = (await db.query(`select relname, relrowsecurity from pg_class where relnamespace = 'public'::regnamespace and relname = any($1)`, [tables])).rows;
  assert.equal(rows.length, tables.length);
  for (const r of rows) assert.ok(r.relrowsecurity, `${r.relname} has row security off`);
});

await test("a member still reads only their own check-ins, payments and terms (so they never hear of anyone else's)", async () => {
  const a = await user(), b = await user();
  await db.query(`insert into gym_check_ins(member_id, method) values ($1, 'manual')`, [b]);
  for (const t of tables) {
    const seen = await as(a, `select count(*)::int n from ${t} where member_id = $1`, [b]);
    assert.equal(seen[0].n, 0, `member read another member's ${t}`);
  }
  const own = await as(b, `select count(*)::int n from gym_check_ins where member_id = $1`, [b]);
  assert.equal(own[0].n, 1);
});

console.log(`\nlive-records: ${passed} passed, ${failed} failed`);
await db.end(); await server.stop();
process.exit(failed ? 1 : 0);
