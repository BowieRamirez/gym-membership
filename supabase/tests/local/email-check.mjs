// The live "email already in use" lookup (0039). Only the check-email Edge Function may call it, and the server applies the limits.
import assert from 'node:assert/strict';
import { start } from './harness.mjs';

const { server, client: db } = await start();
let passed = 0, failed = 0;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (error) { failed++; console.log('  FAIL', name, error.message); }
}
async function asRole(role, sql, values = []) {
  await db.query('begin');
  try {
    await db.query(`set local role ${role}`);
    const result = await db.query(sql, values); await db.query('commit'); return result;
  } catch (error) { await db.query('rollback'); throw error; }
}
const check = async (ip, email) => (await asRole('service_role', 'select check_email_in_use($1,$2) as in_use', [ip, email])).rows[0].in_use;
const reset = () => db.query('truncate email_checks');

await db.query(`insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values('taken@demo.io','{"full_name":"Taken User"}','{}'),('Mixed.Case@demo.io','{"full_name":"Mixed Case"}','{}')`);

try {
  await test('finds an address in any case and with surrounding spaces, and only an exact match', async () => {
    await reset();
    assert.equal(await check('1.1.1.1', 'taken@demo.io'), true);
    assert.equal(await check('1.1.1.1', ' TAKEN@demo.io '), true);
    assert.equal(await check('1.1.1.1', 'mixed.case@demo.io'), true);
    assert.equal(await check('1.1.1.1', 'free@demo.io'), false);
    assert.equal(await check('1.1.1.1', '%@demo.io'), false);
  });
  await test('hostile text is only ever compared, never run', async () => {
    await reset();
    assert.equal(await check(`x'; drop table profiles; --`, `x'; drop table profiles; --`), false);
    assert.ok((await db.query(`select to_regclass('public.profiles') as t`)).rows[0].t);
  });
  await test('an empty or oversized address is refused', async () => {
    await reset();
    await assert.rejects(check('1.1.1.1', ''), /bad_input/);
    await assert.rejects(check('1.1.1.1', null), /bad_input/);
    await assert.rejects(check('1.1.1.1', 'a'.repeat(101)), /bad_input/);
  });
  await test('the app cannot call it or read its table: anonymous and signed-in users are both refused', async () => {
    for (const role of ['anon', 'authenticated']) {
      await assert.rejects(asRole(role, `select check_email_in_use('1.1.1.1','taken@demo.io')`), /permission denied/);
      await assert.rejects(asRole(role, 'select * from email_checks'), /permission denied/);
      await assert.rejects(asRole(role, `insert into email_checks(ip_hash) values('x')`), /permission denied/);
    }
  });
  await test('one address is limited to 30 lookups in 10 minutes, and other addresses are unaffected', async () => {
    await reset();
    for (let i = 0; i < 30; i++) await check('2.2.2.2', `free${i}@demo.io`);
    await assert.rejects(check('2.2.2.2', 'free@demo.io'), /rate_limited/);
    assert.equal(await check('3.3.3.3', 'taken@demo.io'), true);
    assert.equal((await db.query('select count(*)::int as n from email_checks')).rows[0].n, 31);   // refused lookups store nothing
  });
  await test('600 lookups in 10 minutes from anywhere stop everyone, so changing or forging the address does not help', async () => {
    await reset();
    await db.query(`insert into email_checks(ip_hash) select 'hash-' || n from generate_series(1,600) n`);
    await assert.rejects(check('4.4.4.4', 'taken@demo.io'), /rate_limited/);
  });
  await test('lookups older than 10 minutes stop counting and are deleted', async () => {
    await reset();
    await db.query(`insert into email_checks(ip_hash,checked_at) select encode(sha256('5.5.5.5'::bytea),'hex'), now()-interval '11 minutes' from generate_series(1,30)`);
    assert.equal(await check('5.5.5.5', 'taken@demo.io'), true);
    assert.equal((await db.query('select count(*)::int as n from email_checks')).rows[0].n, 1);
  });
  await test('neither the network address nor the email is stored as typed', async () => {
    await reset();
    await check('6.6.6.6', 'taken@demo.io');
    const row = (await db.query('select to_jsonb(e)::text as text from email_checks e')).rows[0].text;
    assert.ok(!row.includes('6.6.6.6') && !row.includes('taken@demo.io'), row);
  });
} finally {
  await db.end(); await server.stop();
}
console.log(`Email check: ${passed} passed, ${failed} failed`);
if (failed) process.exitCode = 1;
