// Owner fixes 2026-11-19 Q12, Q13 (0107): check_discount_code checks a code as it's typed, without writing to payments,
// returning percent/amount_off/you_pay or the refusal reason, limited to 20 checks in 10 minutes per member.
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
  [`dc-${++serial}@test.io`, JSON.stringify({ full_name: 'Discount Person', terms_version: '2026-11-15' }), JSON.stringify(role ? { role } : {})])).rows[0].id;
async function test(name, fn) {
  try { await fn(); passed++; console.log('  ok  ', name); }
  catch (e) { failed++; console.log('  FAIL', name, e.message); }
}

try {
  const admin = await user('admin');
  const pkg = (await db.query(`insert into membership_packages(name,price,duration_days) values('DC Monthly',1500,30) returning membership_package_id id`)).rows[0].id;
  const amenity = (await db.query(`insert into amenities(name,price) values('DC Pool',400) returning amenity_id id`)).rows[0].id;
  const future = new Date(Date.now() + 7 * 86400_000).toISOString();

  await as(admin, `select create_discount('SAVE10',10,$1,5,true,$2::jsonb)`, [future, JSON.stringify([{ kind: 'package', id: null }])]);
  await as(admin, `select create_discount('POOL20',20,$1,5,true,$2::jsonb)`, [future, JSON.stringify([{ kind: 'amenity', id: Number(amenity) }])]);
  await as(admin, `select create_discount('ONEUSE',15,$1,1,true,$2::jsonb)`, [future, JSON.stringify([{ kind: 'package', id: null }])]);
  await db.query(`insert into discounts(code,percent,starts_at,expires_at,max_uses) values('OLD10',10,now() - interval '2 days',now() - interval '1 day',5)`);
  await db.query(`insert into discount_items(discount_id,kind,item_id) select discount_id,'package',null from discounts where code='OLD10'`);

  await test('a good code returns percent, amount_off and you_pay and writes nothing to payments', async () => {
    const m = await user(null);
    const r = (await as(m, `select * from check_discount_code('save10','package',$1)`, [pkg]))[0];
    assert.equal(r.reason, null);
    assert.equal(r.percent, 10);
    assert.equal(Number(r.amount_off), 150);
    assert.equal(Number(r.you_pay), 1350);
    assert.equal((await db.query('select count(*)::int n from payments where member_id = $1', [m])).rows[0].n, 0);
  });

  await test('each refusal returns its own reason', async () => {
    const m = await user(null);
    assert.equal((await as(m, `select * from check_discount_code('NOPE','package',$1)`, [pkg]))[0].reason, 'invalid_code');
    assert.equal((await as(m, `select * from check_discount_code('OLD10','package',$1)`, [pkg]))[0].reason, 'code_expired');
    assert.equal((await as(m, `select * from check_discount_code('POOL20','package',$1)`, [pkg]))[0].reason, 'code_not_for_item');

    // already used by this member
    await as(m, `select * from avail_membership($1,'ONEUSE')`, [pkg]);
    assert.equal((await as(m, `select * from check_discount_code('ONEUSE','package',$1)`, [pkg]))[0].reason, 'code_already_used');

    // limit reached for another member
    const m2 = await user(null);
    assert.equal((await as(m2, `select * from check_discount_code('ONEUSE','package',$1)`, [pkg]))[0].reason, 'code_limit_reached');
  });

  await test('an amenity code works for its amenity', async () => {
    const m = await user(null);
    const r = (await as(m, `select * from check_discount_code('POOL20','amenity',$1)`, [amenity]))[0];
    assert.equal(r.reason, null);
    assert.equal(r.percent, 20);
    assert.equal(Number(r.amount_off), 80);
    assert.equal(Number(r.you_pay), 320);
  });

  await test('the 21st check in 10 minutes is refused with too_many_checks (even wrong guesses count)', async () => {
    const m = await user(null);
    for (let i = 0; i < 20; i++) {
      const r = (await as(m, `select * from check_discount_code($1,'package',$2)`, [`GUESS${i}`, pkg]))[0];
      assert.equal(r.reason, 'invalid_code');
    }
    await assert.rejects(as(m, `select * from check_discount_code('SAVE10','package',$1)`, [pkg]), /too_many_checks/);
  });

  await test('anon cannot call check_discount_code', async () => {
    await db.query('begin');
    try {
      await db.query('set local role anon');
      await assert.rejects(db.query(`select * from check_discount_code('SAVE10','package',$1)`, [pkg]), /permission denied/);
    } finally { await db.query('rollback'); }
  });
} finally {
  console.log(`\ndiscount-check: ${passed} passed, ${failed} failed`);
  if (failed) process.exitCode = 1;
  await db.end(); await server.stop();
}
