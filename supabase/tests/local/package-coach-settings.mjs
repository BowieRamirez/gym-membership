// Phase 9 (E3, E4, E12): package includes_coach, coach_addon_price, new_package_version carry-over, and gym_settings coach limits.
import { start } from './harness.mjs';
import assert from 'node:assert/strict';

const { server, client: db } = await start();
let passed = 0, failed = 0, serial = 0;
async function as(uid, sql, args = []) {
  await db.query('begin');
  try {
    await db.query('set local role authenticated');
    await db.query(`select set_config('request.jwt.claim.sub', $1, true)`, [uid]);
    const r = await db.query(sql, args); await db.query('commit'); return r.rows;
  } catch (e) { await db.query('rollback'); throw e; }
}
const user = async (role) => (await db.query('insert into auth.users(email,raw_user_meta_data,raw_app_meta_data) values($1,$2,$3) returning id',
  [`pcs-${++serial}@test.io`, JSON.stringify({ full_name: 'Pkg Person', terms_version: '2026-11-15' }), JSON.stringify(role ? { role } : {})])).rows[0].id;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}

try {
  const admin = await user('admin');
  const member = await user(null);

  await test('Coach included on cannot store an add-on price; Coach included off stores add-on price and carries on new_package_version', async () => {
    await assert.rejects(
      as(admin, `insert into membership_packages(name,price,duration_days,includes_coach,coach_addon_price) values('Bad Inc',1500,30,true,200)`),
      /membership_packages_coach_addon_check/
    );
    await assert.rejects(
      as(member, `insert into membership_packages(name,price,duration_days,includes_coach) values('Mem Try',1500,30,true)`),
      /row-level security|permission denied/
    );

    const incId = (await as(admin, `insert into membership_packages(name,description,price,duration_days,includes_coach) values('VIP','Full VIP access',5000,30,true) returning membership_package_id id`))[0].id;
    await as(admin, `select publish_catalog_item('plan', $1)`, [incId]);
    const v2 = (await as(admin, `select new_package_version($1) id`, [incId]))[0].id;
    const row2 = (await db.query(`select includes_coach, coach_addon_price from membership_packages where membership_package_id=$1`, [v2])).rows[0];
    assert.equal(row2.includes_coach, true);
    assert.equal(row2.coach_addon_price, null);

    const plainId = (await as(admin, `insert into membership_packages(name,description,price,duration_days,includes_coach,coach_addon_price) values('Basic','Basic gym access',1500,30,false,250) returning membership_package_id id`))[0].id;
    await as(admin, `select publish_catalog_item('plan', $1)`, [plainId]);
    const pv2 = (await as(admin, `select new_package_version($1) id`, [plainId]))[0].id;
    const prow2 = (await db.query(`select includes_coach, coach_addon_price from membership_packages where membership_package_id=$1`, [pv2])).rows[0];
    assert.equal(prow2.includes_coach, false);
    assert.equal(Number(prow2.coach_addon_price), 250);

    const mRead = (await as(member, `select includes_coach, coach_addon_price from membership_packages where membership_package_id=$1`, [plainId]))[0];
    assert.equal(mRead.includes_coach, false);
    assert.equal(Number(mRead.coach_addon_price), 250);
  });

  await test('gym_settings has coach limits (1, 2, 6) and admin can update or clear daily max', async () => {
    const def = (await db.query(`select max_coach_sessions_per_day, max_upcoming_coach_sessions, max_coach_hours_per_day from gym_settings where id=1`)).rows[0];
    assert.deepEqual([def.max_coach_sessions_per_day, def.max_upcoming_coach_sessions, def.max_coach_hours_per_day], [1, 1, 6]);

    await as(admin, `update gym_settings set max_coach_sessions_per_day=2, max_upcoming_coach_sessions=4, max_coach_hours_per_day=null where id=1`);
    const upd = (await db.query(`select max_coach_sessions_per_day, max_upcoming_coach_sessions, max_coach_hours_per_day from gym_settings where id=1`)).rows[0];
    assert.deepEqual([upd.max_coach_sessions_per_day, upd.max_upcoming_coach_sessions, upd.max_coach_hours_per_day], [2, 4, null]);
  });

  console.log(`\n${passed} passed, ${failed} failed`);
  if (failed > 0) process.exitCode = 1;
} finally { await db.end(); await server.stop(); }
