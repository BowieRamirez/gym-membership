// Phase 3 (D4/D5): discount codes have a use limit. Pending, verified and voided payments hold a place;
// rejected, expired and member-cancelled ones give it back. A code that reaches its limit is archived for good.
import { start } from './harness.mjs';
import assert from 'node:assert/strict';
import pg from 'pg';

let legacy;
const { server, client: db } = await start({ beforeMigration: async (client, file) => {
  if (file === '0026_discount_limits.sql')   // a code made before limits existed
    legacy = (await client.query(`insert into discounts(code,percent,expires_at) values('LEGACY',10,now()+interval '30 days') returning discount_id`)).rows[0].discount_id;
} });
let passed = 0, failed = 0, serial = 0;
const clients = [];
async function as(uid, sql, args = [], client = db) {
  await client.query('begin');
  try {
    await client.query('set local role authenticated');
    await client.query(`select set_config('request.jwt.claim.sub', $1, true)`, [uid]);
    const r = await client.query(sql, args);
    await client.query('commit');
    return r.rows;
  } catch (e) { await client.query('rollback'); throw e; }
}
async function user(role = 'member') {
  return (await db.query(`insert into auth.users(email,raw_user_meta_data,raw_app_meta_data)
    values($1,'{"full_name":"Test Person"}',$2) returning id`, [`p3-${++serial}@test.io`, JSON.stringify({role})])).rows[0].id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}
async function connection() {
  const client = new pg.Client({ host: db.connectionParameters.host, port: db.connectionParameters.port, user: 'postgres', password: 'pw', database: 'postgres' });
  await client.connect(); clients.push(client); return client;
}
async function waitBlocked(client) {
  for (let n = 0; n < 100; n++) {
    if ((await db.query('select cardinality(pg_blocking_pids($1)) n', [client.processID])).rows[0].n > 0) return;
    await new Promise(resolve => setTimeout(resolve, 20));
  }
  throw new Error('Expected concurrent checkout to wait for the code lock');
}
const admin = await user('admin'), desk = await user('employee');
const monthly = (await db.query(`insert into membership_packages(name,price,duration_days)
  values('Phase Three Monthly',1000,30) returning membership_package_id`)).rows[0].membership_package_id;
const addCode = (code, limit) => as(admin, `insert into discounts(code,percent,expires_at,max_uses) values($1,10,now()+interval '30 days',$2) returning *`, [code, limit]);
const code = async c => (await db.query('select * from discounts where code=$1', [c])).rows[0];
const buy = async (uid, c, client = db) => (await as(uid, 'select * from extend_membership($1,$2)', [monthly, c], client))[0];
const verify = pay => as(desk, 'select verify_payment($1,true)', [pay.payment_id]);
const reject = pay => as(desk, `select verify_payment($1,false,'Wrong reference')`, [pay.payment_id]);
const usage = async id => (await as(admin, 'select * from discount_usage()')).find(r => r.id === String(id) || r.id === id);

try {
  await test('new codes need a positive limit; existing codes stay unlimited', async () => {
    await assert.rejects(addCode('NOLIMIT', null), /limit_required/);
    await assert.rejects(addCode('ZERO', 0), /check|limit/);
    assert.equal((await code('LEGACY')).max_uses, null);
    for (let n = 0; n < 3; n++) { const uid = await user(); await verify(await buy(uid, 'LEGACY')); }
    assert.equal((await code('LEGACY')).is_active, true);
  });
  await test('pending payments hold a place; rejection gives it back', async () => {
    await addCode('TWO', 2);
    const a = await buy(await user(), 'TWO'), b = await buy(await user(), 'TWO');
    await assert.rejects(buy(await user(), 'TWO'), /code_limit_reached/);
    const id = (await code('TWO')).discount_id;
    const u = await usage(id); assert.equal(Number(u.used), 0); assert.equal(Number(u.pending), 2);
    await reject(a);
    const c = await buy(await user(), 'TWO');
    assert.ok(c.discount_id);
    assert.equal((await code('TWO')).is_active, true);
    void b;
  });
  await test('the last verified use archives the code with "Limit reached" and it cannot be restored', async () => {
    await addCode('ONCE', 1);
    const pay = await buy(await user(), 'ONCE');
    assert.equal((await code('ONCE')).is_active, true);
    await verify(pay);
    const d = await code('ONCE');
    assert.equal(d.is_active, false); assert.equal(d.archive_reason, 'Limit reached'); assert.ok(d.archived_at);
    await assert.rejects(as(admin, `select restore_archived_record('code',$1)`, [String(d.discount_id)]), /record_not_restorable/);
    await assert.rejects(as(admin, 'update discounts set max_uses=5 where discount_id=$1', [d.discount_id]), /record_archived/);
    const row = (await as(admin, `select * from archive_records('code','',null,null,'Limit',0,31)`)).find(r => r.record_id === String(d.discount_id));
    assert.equal(row.can_restore, false);
  });
  await test('a counter payment recorded as verified also uses up the last place', async () => {
    await addCode('DESK1', 1);
    const uid = await user();
    await as(desk, `select record_counter_payment('membership','cash',$1,$2,null,null,'DESK1')`, [uid, monthly]);
    assert.equal((await code('DESK1')).is_active, false);
  });
  await test('voided uses stay counted, and the same member cannot reuse the code after a void', async () => {
    await addCode('VOIDME', 3);
    const uid = await user(), pay = await buy(uid, 'VOIDME'); await verify(pay);
    await as(admin, `select void_payment($1,'Wrong payment')`, [pay.payment_id]);
    assert.equal(Number((await usage((await code('VOIDME')).discount_id)).used), 1);
    await assert.rejects(buy(uid, 'VOIDME'), /invalid_code/);
  });
  await test('limit edits: never below places in use, never back to unlimited, legacy codes can gain a limit', async () => {
    await addCode('EDIT', 5);
    const id = (await code('EDIT')).discount_id;
    await verify(await buy(await user(), 'EDIT')); await buy(await user(), 'EDIT');
    await assert.rejects(as(admin, 'update discounts set max_uses=1 where discount_id=$1', [id]), /limit_below_used/);
    await assert.rejects(as(admin, 'update discounts set max_uses=null where discount_id=$1', [id]), /limit_required/);
    await as(admin, 'update discounts set max_uses=3 where discount_id=$1', [id]);
    assert.equal((await code('EDIT')).max_uses, 3);
    await as(admin, 'update discounts set max_uses=10 where discount_id=$1', [legacy]);
    assert.equal((await code('LEGACY')).max_uses, 10);
  });
  await test('setting the limit to the verified count archives the code at once', async () => {
    await addCode('SHRINK', 5);
    await verify(await buy(await user(), 'SHRINK'));
    await as(admin, `update discounts set max_uses=1 where code='SHRINK'`);
    const d = await code('SHRINK'); assert.equal(d.is_active, false); assert.equal(d.archive_reason, 'Limit reached');
  });
  await test('only discount managers can read usage', async () => {
    await assert.rejects(as(desk, 'select * from discount_usage()'), /forbidden/);
    await assert.rejects(as(await user(), 'select * from discount_usage()'), /forbidden/);
  });
  await test('two members racing for the last place: exactly one gets it', async () => {
    await addCode('LAST', 1);
    const [one, two] = [await connection(), await connection()];
    const [m1, m2] = [await user(), await user()];
    await one.query('begin'); await one.query('set local role authenticated');
    await one.query(`select set_config('request.jwt.claim.sub',$1,true)`, [m1]);
    await one.query('select * from extend_membership($1,$2)', [monthly, 'LAST']);
    const second = buy(m2, 'LAST', two).then(() => 'ok', e => e.message);
    await waitBlocked(two);
    await one.query('commit');
    assert.match(await second, /code_limit_reached/);
    const u = await usage((await code('LAST')).discount_id); assert.equal(Number(u.used) + Number(u.pending), 1);
  });
} finally {
  for (const c of clients) await c.end();
  await db.end(); await server.stop();
}
console.log(`\nDiscount limits: ${passed} passed, ${failed} failed`);
process.exitCode = failed ? 1 : 0;
