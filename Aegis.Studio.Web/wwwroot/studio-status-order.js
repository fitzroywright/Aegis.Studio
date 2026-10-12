/* Filter choices follow operational event order, independently of row sorting. */
(()=>{
 const jobStages=['New','Received','Unreviewed','Pending','Pending Confirmation','Awaiting Deposit','Confirmed','Needs Assignment','Unassigned','Needs Photographer Assignment','Photographer Assigned','Awaiting Scheduling','Scheduled','Shooting','Photography In Progress','Shoot Complete','Awaiting Upload','Awaiting Production Review','In Production','Active Production','Waiting On Client','Awaiting Approval','Ready For Delivery','Delivered','Completed','Closed','Archived','Changes Requested','Needs Retake','Reshoot Required','Deferred','Blocked','Rejected','Declined','Cancelled'];
 const normalize=value=>String(value??'').toLowerCase().replace(/[\s_-]/g,'');
 const positions=new Map(jobStages.map((value,index)=>[normalize(value),index]));
 const compare=(a,b)=>(positions.get(normalize(a))??jobStages.length)-(positions.get(normalize(b))??jobStages.length);
 const sort=values=>[...new Set(values)].sort(compare);
 window.StudioStatusOrder={compare,sort};
})();
