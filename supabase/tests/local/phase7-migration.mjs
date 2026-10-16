import { start } from './harness.mjs';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const preflightSql = readFileSync(new URL('../../preflight/0018_preflight.sql', import.meta.url), 'utf8').split('-- Rolled-back dry run')[0];

let owner, desk, member, legacy, pending, packageId, original, renewals, oldWindows;
async function as(db, uid, sql, args=[]) {
  await db.query('begin');
  try {
    await db.query('set local role authenticated');
    await db.query(`select set_config('request.jwt.claim.sub',$1,true)`,[uid]);
    const r = await db.query(sql,args); await db.query('commit'); return r.rows;
  } catch(e) { await db.query('rollback'); throw e; }
}
const { server, client: db } = await start({ beforeMigration: async (db, name, sql) => {
  if (!name.startsWith('0018_')) return;
  async function user(email, role) { return (await db.query(`insert into auth.users(email,raw_user_meta_data,raw_app_meta_data)
    values($1,'{"full_name":"Migration Person"}',$2) returning id`,[email,JSON.stringify({role})])).rows[0].id; }
  owner=await user('migration-owner@test.io','admin'); desk=await user('migration-desk@test.io','employee');
  member=await user('migration-member@test.io','member'); legacy=await user('migration-legacy@test.io','member'); pending=await user('migration-pending@test.io','member');
  packageId=(await db.query(`insert into membership_packages(name,price,duration_days) values('Historic Monthly',1000,30) returning membership_package_id`)).rows[0].membership_package_id;
  original=(await as(db,desk,`select * from record_counter_payment('membership','cash',$1,$2)`,[member,packageId]))[0];
  renewals=[];
  for(let i=0;i<2;i++) {
    const p=(await as(db,member,'select * from renew_membership($1)',[original.user_membership_package_id]))[0];
    await as(db,desk,'select verify_payment($1,true)',[p.payment_id]); renewals.push(p);
  }
  oldWindows=(await db.query(`select previous_ends_at,new_starts_at,new_ends_at from membership_renewals order by renewal_id`)).rows;
  await as(db,pending,'select * from avail_membership($1)',[packageId]);
  await db.query(`insert into user_membership_packages(member_id,membership_package_id,status,starts_at,ends_at)
    values($1,$2,'active',now()-interval '1 day',now()+interval '29 days')`,[legacy,packageId]);

  // The owner's pre-flight query must read zero problems for a healthy history, and the one legacy member without a payment as information.
  const preflight = async () => Object.fromEntries((await db.query(preflightSql)).rows.map(r => [r.check.split(' (')[0], Number(r.n)]));
  const healthy = await preflight();
  assert.deepEqual(Object.values(healthy).slice(0,4), [0,0,0,0]); assert.equal(Object.values(healthy)[4], 1);
  // Each malformed-history check runs the actual migration, then rolls all its DDL back.
  await db.query('begin');
  try {
    await db.query(`insert into user_membership_packages(member_id,membership_package_id,status,starts_at,ends_at)
      select member_id,membership_package_id,'expired',starts_at,ends_at from user_membership_packages where user_membership_package_id=$1`,[original.user_membership_package_id]);
    await assert.rejects(db.query(sql),/overlapping_membership_history/);
  } finally { await db.query('rollback'); }
  await db.query('begin');
  try {
    await db.query(`update user_membership_packages set status='cancelled' where user_membership_package_id=$1`,[original.user_membership_package_id]);
    await assert.rejects(db.query(sql),/ambiguous_membership_history/);
  } finally { await db.query('rollback'); }
  // A voided earlier renewal while a later one stays completed: the old void put the membership end back, so converting the later
  // renewal's window into a term would hand out days nobody paid for. The migration must refuse that history, not guess.
  await db.query('begin');
  try {
    await db.query('set local role authenticated'); await db.query(`select set_config('request.jwt.claim.sub',$1,true)`,[owner]);
    await db.query('select void_payment($1,$2)',[renewals[0].payment_id,'Voided the first renewal']);
    await db.query('reset role');
    assert.equal(Object.values(await preflight())[3], 1);
    await assert.rejects(db.query(sql),/ambiguous_membership_history/);
  } finally { await db.query('rollback'); }
  // A completed renewal whose payment is not verified would be dropped from the terms silently.
  await db.query('begin');
  try {
    await db.query(`update payments set status='pending' where payment_id=$1`,[renewals[1].payment_id]);
    await assert.rejects(db.query(sql),/ambiguous_membership_history/);
  } finally { await db.query('rollback'); }
  console.log('  ok   real migration refuses a voided earlier renewal and a completed renewal without a verified payment');
  console.log('  ok   real migration refuses overlapping or cancelled-but-paid legacy history transactionally');
}});
try {
  const terms=await as(db,member,'select * from membership_terms order by starts_at');
  assert.equal(terms.length,3);
  assert.deepEqual(terms.map(t=>t.payment_id),[original.payment_id,...renewals.map(p=>p.payment_id)]);
  assert.equal(+terms[0].ends_at,+oldWindows[0].previous_ends_at);
  for(let i=0;i<2;i++) {
    assert.equal(+terms[i+1].starts_at,+oldWindows[i].new_starts_at);
    assert.equal(+terms[i+1].ends_at,+oldWindows[i].new_ends_at);
  }
  for(const t of terms) { assert.equal(t.snapshot_source,'legacy_catalog'); assert.equal(Number(t.amount_paid),1000); }
  assert.equal((await as(db,member,'select is_active_member() active'))[0].active,true);
  assert.equal((await as(db,pending,'select * from membership_terms')).length,0);
  const [free]=await as(db,legacy,'select * from membership_terms'); assert.equal(free.payment_id,null);
  await assert.rejects(as(db,legacy,'select request_membership_refund($1,$2)',[free.term_id,'No receipt']),/not_refundable/);
  await assert.rejects(as(db,member,`update payments set membership_duration_days=1`),/permission denied/);
  const snapshots=await as(db,member,'select membership_package_name,membership_duration_days from payments');
  assert.equal(snapshots.length,3); assert.ok(snapshots.every(p=>p.membership_package_name==='Historic Monthly' && p.membership_duration_days===30));
  console.log('  ok   migration preserves paid original/renewal dates, pending purchases, legacy access and receipt snapshots');
  const [pendingPayment]=await as(db,pending,"select * from payments where status='pending'");
  assert.equal(pendingPayment.membership_snapshot_source,'legacy_catalog');
  await as(db,desk,'select verify_payment($1,true)',[pendingPayment.payment_id]);
  const [activated]=await as(db,pending,'select * from membership_terms');
  assert.equal(activated.snapshot_source,'legacy_catalog');
  assert.equal(activated.payment_id,pendingPayment.payment_id);
  console.log('  ok   verifying a pre-migration pending payment preserves its legacy snapshot provenance');
  console.log('\nPhase 7 populated-history migration: all checks passed');
} finally { await db.end(); await server.stop(); }
