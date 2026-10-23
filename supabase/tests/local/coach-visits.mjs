// Phase 8 (D9): physical coach visits. The coach or the front desk checks a coach in/out, each change keeps its actor,
// one open visit per coach, check-in/out twice is harmless, the closing-time job closes forgotten visits,
// and a new hire needs the coach in the gym while existing hires are untouched.
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
    values($1,'{"full_name":"Test Person"}',$2) returning id`, [`p8-${++serial}@test.io`, JSON.stringify({role})])).rows[0].id;
}
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.error('  FAIL', name, e.message); }
}
const desk = await user('employee'), coach = await user('coach'), other = await user('coach'), member = await user();
const open = async c => (await db.query('select * from coach_visits where coach_id=$1 and checked_out_at is null', [c])).rows;
// gym hours that include / exclude "now" (Manila); the gym cannot be open between 23:30 and midnight
const minute = (await db.query(`select extract(hour from now() at time zone 'Asia/Manila')*60 + extract(minute from now() at time zone 'Asia/Manila') m`)).rows[0].m;
const openNow = () => db.query(`update gym_settings set opens_at='00:00', closes_at='23:30'`);
const closedNow = () => minute >= 60 ? db.query(`update gym_settings set opens_at='00:00', closes_at='00:30'`)
                                     : db.query(`update gym_settings set opens_at='01:00', closes_at='23:30'`);

try {
  await test('check-in is refused while the gym is closed', async () => {
    await closedNow();
    await assert.rejects(as(coach, 'select coach_check_in()'), /gym_closed/);
    assert.equal((await open(coach)).length, 0);
  });
  if (minute < 23 * 60 + 30) {
    await test('a coach checks in once; a second check-in returns the same visit', async () => {
      await openNow();
      const a = (await as(coach, 'select coach_check_in() id'))[0].id;
      const b = (await as(coach, 'select coach_check_in() id'))[0].id;
      assert.equal(a, b);
      const v = await open(coach);
      assert.equal(v.length, 1); assert.equal(v[0].checked_in_by, coach);
    });
    await test('the coach checks out once; a second checkout is harmless', async () => {
      assert.equal((await as(coach, 'select coach_check_out() ok'))[0].ok, true);
      assert.equal((await as(coach, 'select coach_check_out() ok'))[0].ok, false);
      const v = (await db.query('select * from coach_visits where coach_id=$1', [coach])).rows[0];
      assert.equal(v.checked_out_by, coach); assert.equal(v.auto_closed, false);
    });
    await test('the front desk checks a coach in and out and is recorded as the actor', async () => {
      await as(desk, 'select coach_check_in($1)', [other]);
      assert.equal((await open(other))[0].checked_in_by, desk);
      await as(desk, 'select coach_check_out($1)', [other]);
      assert.equal((await db.query('select checked_out_by from coach_visits where coach_id=$1', [other])).rows[0].checked_out_by, desk);
    });
  } else console.log('  skip  open-gym checks: the gym cannot be open between 23:30 and midnight Manila');
  await test('members and other coaches cannot check a coach in or out, or write visits directly', async () => {
    await openNow();
    await assert.rejects(as(member, 'select coach_check_in($1)', [coach]), /forbidden/);
    await assert.rejects(as(other, 'select coach_check_out($1)', [coach]), /forbidden/);
    await assert.rejects(as(member, 'select coach_check_in()'), /not_found/);   // not a coach
    await assert.rejects(as(coach, 'insert into coach_visits(coach_id,checked_in_by) values($1,$1)', [coach]), /permission denied|42501/);
    await assert.rejects(as(desk, 'select close_coach_visits()'), /permission denied|42501/);
  });
  await test('only one visit per coach can be open', async () => {
    await db.query('insert into coach_visits(coach_id,checked_in_by) values($1,$1)', [coach]);
    await assert.rejects(db.query('insert into coach_visits(coach_id,checked_in_by) values($1,$1)', [coach]), /coach_visits_one_open/);
    await db.query('update coach_visits set checked_out_at=now() where coach_id=$1 and checked_out_at is null', [coach]);
  });
  await test('everyone sees who is in now; past visits only the coach and staff', async () => {
    await db.query('insert into coach_visits(coach_id,checked_in_by) values($1,$1)', [other]);
    const seen = await as(member, 'select coach_id, checked_out_at from coach_visits');
    assert.ok(seen.every(v => v.checked_out_at === null)); assert.ok(seen.some(v => v.coach_id === other));
    assert.ok((await as(coach, 'select 1 from coach_visits where coach_id=$1 and checked_out_at is not null', [coach])).length > 0);
    assert.ok((await as(desk, 'select 1 from coach_visits where checked_out_at is not null')).length > 0);
    await db.query('update coach_visits set checked_out_at=now() where coach_id=$1 and checked_out_at is null', [other]);
  });
  await test('the closing job closes a forgotten visit at that day\'s closing time and leaves today\'s open', async () => {
    await db.query(`update gym_settings set opens_at='06:00', closes_at='22:00'`);
    const yesterday = (await db.query(`select (((now() at time zone 'Asia/Manila')::date - 1) + time '10:00') at time zone 'Asia/Manila' t`)).rows[0].t;
    await db.query('insert into coach_visits(coach_id,checked_in_by,checked_in_at) values($1,$1,$2)', [coach, yesterday]);
    await db.query(`insert into coach_visits(coach_id,checked_in_by,checked_in_at) values($1,$1,now() + interval '1 day')`, [other]);   // closes tomorrow night
    assert.equal((await db.query('select close_coach_visits() n')).rows[0].n, 1);
    const v = (await db.query('select * from coach_visits where coach_id=$1 and checked_in_at=$2', [coach, yesterday])).rows[0];
    assert.equal(v.auto_closed, true); assert.equal(v.checked_out_by, null);
    assert.equal(new Date(v.checked_out_at).getTime(), new Date(yesterday).getTime() + 12 * 36e5);   // 10:00 -> 22:00
    assert.equal((await open(other)).length, 1);
    await db.query('delete from coach_visits where coach_id=$1 and checked_out_at is null', [other]);
  });
  await test('a new hire needs the coach in the gym; an existing hire survives checkout', async () => {
    const pack = (await db.query(`insert into membership_packages(name,price,duration_days) values('P8 Monthly',1000,30) returning membership_package_id`)).rows[0].membership_package_id;
    const pay = (await as(member, 'select * from extend_membership($1,null)', [pack]))[0];
    await as(desk, 'select verify_payment($1,true)', [pay.payment_id]);
    await assert.rejects(as(member, 'select hire_coach($1)', [coach]), /coach_not_in_gym/);
    await db.query('insert into coach_visits(coach_id,checked_in_by) values($1,$1)', [coach]);
    await as(member, 'select hire_coach($1)', [coach]);
    await db.query('update coach_visits set checked_out_at=now() where coach_id=$1 and checked_out_at is null', [coach]);
    assert.equal((await as(member, `select status from coach_hires where coach_id=$1`, [coach]))[0].status, 'active');
  });
} finally {
  await db.end(); await server.stop();
}
console.log(`\nCoach visits: ${passed} passed, ${failed} failed`);
process.exitCode = failed ? 1 : 0;
