// G28 (owner, 2026-11-15; 0071): the Coaches page tags hired coaches. taken_coaches() says which coaches have an active
// hire, never who hired them; hiring is unchanged, so a coach can still take more members.
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
    values($1,'{"full_name":"Test Person"}',$2) returning id`, [`g28t-${++serial}@test.io`, JSON.stringify({ role })])).rows[0].id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}
const desk = await user('employee'), coach = await user('coach'), ana = await user(), ben = await user();
const pack = (await db.query(`insert into membership_packages(name,price,duration_days) values('G28 Monthly',1000,30) returning membership_package_id id`)).rows[0].id;
for (const m of [ana, ben]) {
  const pay = (await as(m, 'select * from extend_membership($1,null)', [pack]))[0];
  await as(desk, 'select verify_payment($1,true)', [pay.payment_id]);
}
await db.query('insert into coach_visits(coach_id,checked_in_by) values($1,$1)', [coach]);
const taken = async (uid) => (await as(uid, 'select taken_coaches() id')).map(r => r.id);

try {
  await test('a coach with no hire is not listed', async () => {
    assert.deepEqual(await taken(ben), []);
  });
  await test('once hired, every member sees the coach listed, and another member can still hire them', async () => {
    await as(coach, 'select answer_hire($1,true)', [(await as(ana, 'select hire_coach($1) id', [coach]))[0].id]);   // G30 (0074): the coach accepts
    assert.deepEqual(await taken(ben), [coach]);
    assert.deepEqual(await taken(ana), [coach]);
    await as(coach, 'select answer_hire($1,true)', [(await as(ben, 'select hire_coach($1) id', [coach]))[0].id]);
    assert.deepEqual(await taken(ben), [coach]);   // listed once
  });
  await test('when every hire ends the coach is no longer listed', async () => {
    for (const m of [ana, ben]) {
      const id = (await as(m, `select coach_hire_id id from coach_hires where coach_id=$1 and status='active'`, [coach]))[0].id;
      await as(m, 'select end_hire($1)', [id]);
    }
    assert.deepEqual(await taken(ana), []);
  });
  await test('taken_coaches is not open to the public', async () => {
    await db.query('begin'); await db.query('set local role anon');
    await assert.rejects(db.query('select taken_coaches()'), /permission denied/);
    await db.query('rollback');
  });
} finally {
  await db.end(); await server.stop();
}
console.log(`\nHired coach tag: ${passed} passed, ${failed} failed`);
process.exitCode = failed ? 1 : 0;
