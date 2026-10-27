'use strict';
document.querySelector('.mobile-toggle')?.addEventListener('click', function () {
  const open = document.querySelector('#sidebar').classList.toggle('open');
  this.setAttribute('aria-expanded', String(open));
});
document.querySelectorAll('form[data-confirm]').forEach(form => form.addEventListener('submit', event => {
  if (!window.confirm(form.dataset.confirm)) event.preventDefault();
}));
document.querySelectorAll('[data-confirm-button]').forEach(button => button.addEventListener('click', event => {
  if (!window.confirm(button.dataset.confirmButton)) event.preventDefault();
}));
if (window.Chart) {
  Chart.defaults.font.family = 'Inter, Segoe UI, Arial, sans-serif';
  Chart.defaults.font.size = 14;
  Chart.defaults.color = '#858585';
  const options = {responsive: true, maintainAspectRatio: false, animation: false, plugins: {legend: {display: false}}, scales: {x: {grid: {display: false}, ticks: {maxTicksLimit: 7}}, y: {grid: {color: '#ededed'}, border: {display: false}, title: {display: true, text: 'Weight (kg)'}}}};
  document.querySelectorAll('.weight-chart').forEach(canvas => {
    const points = JSON.parse(canvas.dataset.points);
    new Chart(canvas, {type: 'line', data: {labels: points.map(p => new Date(p.At).toLocaleDateString('en-GB', {day: 'numeric', month: 'short', timeZone: 'UTC'})), datasets: [{label: 'Weight (kg)', data: points.map(p => p.Weight), borderColor: '#4b79cc', backgroundColor: '#e9f0fc80', fill: true, borderWidth: 2, pointRadius: points.length > 40 ? 0 : 3, pointBackgroundColor: '#fff', pointBorderWidth: 2, tension: 0.15}]}, options});
  });
  document.querySelectorAll('.activity-chart').forEach(canvas => {
    const points = JSON.parse(canvas.dataset.points);
    new Chart(canvas, {type: 'bar', data: {labels: points.map(p => p.Day), datasets: [{label: 'Known calories burned', data: points.map(p => p.Burned), backgroundColor: '#6a8bd1', borderRadius: 3}, {label: 'Known energy consumed', data: points.map(p => p.Eaten), backgroundColor: '#b8c8e9', borderRadius: 3}]}, options: {responsive: true, maintainAspectRatio: false, animation: false, plugins: {legend: {position: 'bottom'}}, scales: {x: {grid: {display: false}, ticks: {maxTicksLimit: 8}}, y: {beginAtZero: true, title: {display: true, text: 'kcal'}, grid: {color: '#ededed'}}}}});
  });
}
document.querySelectorAll('.sidebar nav>a').forEach(link => {
  const url = new URL(link.href);
  const active = (url.pathname === location.pathname || (url.pathname !== '/Dashboard' && location.pathname.startsWith(url.pathname + '/'))) && (!url.searchParams.has('kind') || url.searchParams.get('kind') === new URLSearchParams(location.search).get('kind'));
  link.classList.toggle('active', active);
  if (active) link.setAttribute('aria-current', 'page');
});
if (window.Chart) document.querySelectorAll('.usage-chart').forEach(canvas => {
  const points = JSON.parse(canvas.dataset.points);
  new Chart(canvas, {type:'bar', data:{labels:points.map(p => p.Day.slice(0,10)),datasets:[{label:'Log entries',data:points.map(p=>p.Entries),backgroundColor:'#6a8bd1',borderRadius:3}]},options:{responsive:true,maintainAspectRatio:false,animation:false,plugins:{legend:{display:false}},scales:{x:{grid:{display:false},ticks:{maxTicksLimit:8}},y:{beginAtZero:true,ticks:{precision:0},grid:{color:'#ededed'}}}}});
});
if (window.Chart) document.querySelectorAll('.bmi-chart').forEach(canvas => {
  const points = JSON.parse(canvas.dataset.points);
  new Chart(canvas, {type:'line',data:{labels:points.map(p=>p.At.slice(0,10)),datasets:[{label:'BMI',data:points.map(p=>p.Bmi),borderColor:'#8c78be',pointRadius:2,tension:0.1,spanGaps:false}]},options:{responsive:true,maintainAspectRatio:false,animation:false,plugins:{legend:{display:false}},scales:{x:{grid:{display:false},ticks:{maxTicksLimit:7}},y:{title:{display:true,text:'BMI'},grid:{color:'#ededed'}}}}});
});
if (window.Chart) document.querySelectorAll('.weekly-chart').forEach(canvas => {
  const points = JSON.parse(canvas.dataset.points);
  new Chart(canvas, {type:'bar',data:{labels:points.map(p=>p.Starting),datasets:[{label:'Workout minutes',data:points.map(p=>p.Minutes),backgroundColor:'#6a8bd1',borderRadius:4}]},options:{responsive:true,maintainAspectRatio:false,animation:false,plugins:{legend:{display:false}},scales:{x:{grid:{display:false},ticks:{maxTicksLimit:8}},y:{beginAtZero:true,title:{display:true,text:'Minutes'},grid:{color:'#ededed'}}}}});
});


if (window.Chart) document.querySelectorAll('.registration-chart,.workload-chart').forEach(canvas => {
  const points = JSON.parse(canvas.dataset.points);
  const workload = canvas.classList.contains('workload-chart');
  new Chart(canvas, {type: 'bar', data: {labels: points.map(p => workload ? p.Name : p.Day.slice(0,10)), datasets: [{label: workload ? 'Assigned clients' : 'Registrations', data: points.map(p => workload ? p.Clients : p.Entries), backgroundColor: '#4378aa', borderRadius: 4}]}, options: {responsive: true, maintainAspectRatio: false, animation: false, plugins: {legend: {display: false}}, scales: {x: {ticks: {maxTicksLimit: 8}}, y: {beginAtZero: true, ticks: {precision: 0}}}}});
});
