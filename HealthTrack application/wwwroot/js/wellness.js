'use strict';
document.querySelectorAll('[data-water-amount]').forEach(button => button.addEventListener('click', () => {
  const field = document.getElementById('WaterMl');
  if (!field) return;
  field.value = button.dataset.waterAmount;
  field.dispatchEvent(new Event('input', {bubbles: true}));
  field.focus();
}));
if (window.Chart) document.querySelectorAll('.wellness-chart').forEach(canvas => {
  const days = JSON.parse(canvas.dataset.points);
  const select = document.getElementById('wellness-metric');
  const colors = {WorkoutMinutes: '#256b55', WaterMl: '#2769ad', SleepMinutes: '#7651aa', Steps: '#9b641c'};
  const chart = new Chart(canvas, {type: 'bar', data: {labels: days.map(d => d.Date), datasets: [{label: 'Workout minutes', data: days.map(d => d.WorkoutMinutes), backgroundColor: colors.WorkoutMinutes, borderRadius: 5}]}, options: {responsive: true, maintainAspectRatio: false, animation: false, plugins: {legend: {display: false}}, scales: {x: {grid: {display: false}}, y: {beginAtZero: true, ticks: {precision: 0}, title: {display: true, text: 'Workout minutes'}}}}});
  select?.addEventListener('change', () => {
    const metric = select.value;
    if (!Object.hasOwn(colors, metric)) return;
    const label = select.options[select.selectedIndex].text;
    chart.data.datasets[0].data = days.map(d => d[metric]);
    chart.data.datasets[0].label = label;
    chart.data.datasets[0].backgroundColor = colors[metric];
    chart.options.scales.y.title.text = label;
    canvas.setAttribute('aria-label', label + ' over seven days. Exact values are in the following table.');
    chart.update();
  });
});
