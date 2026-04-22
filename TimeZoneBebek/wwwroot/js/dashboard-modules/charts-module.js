(function (global) {
    const createChartsRuntime = (deps) => {
        let incidentChart = null;
        let epsChart = null;

        const initIncidentChart = () => {
            if (!deps.chartRef) return null;
            incidentChart = new ApexCharts(deps.chartRef.value, {
                series: [{ name: "Incidents", data: [0, 0, 0, 0, 0, 0, 0, 0] }],
                chart: { type: "area", height: 80, sparkline: { enabled: true } },
                stroke: { curve: "smooth", width: 2 },
                fill: { type: "gradient", gradient: { shadeIntensity: 1, opacityFrom: 0.7, opacityTo: 0.1, stops: [0, 90, 100] } },
                colors: ["#f1c40f"]
            });
            incidentChart.render();
            return incidentChart;
        };

        const initEpsChart = () => {
            if (!deps.epsChartRef) return null;
            epsChart = new ApexCharts(deps.epsChartRef.value, {
                series: [{ name: "EPS", data: deps.epsDataSeries.value }],
                chart: {
                    type: "bar",
                    height: 60,
                    sparkline: { enabled: true },
                    animations: { enabled: true, easing: "linear", dynamicAnimation: { speed: 1000 } }
                },
                plotOptions: { bar: { columnWidth: "70%", borderRadius: 2 } },
                colors: ["#00ffcc"],
                tooltip: { fixed: { enabled: false }, x: { show: false }, marker: { show: false } }
            });
            epsChart.render();
            return epsChart;
        };

        const updateIncidents = (series) => {
            incidentChart?.updateSeries([{ data: series || [0, 0, 0, 0, 0, 0, 0, 0] }]);
        };

        const updateEps = (series) => {
            epsChart?.updateSeries([{ data: series || [] }]);
        };

        const stop = () => {
            incidentChart?.destroy?.();
            epsChart?.destroy?.();
            incidentChart = null;
            epsChart = null;
        };

        return { initIncidentChart, initEpsChart, updateIncidents, updateEps, stop };
    };

    global.DashboardChartsModule = { createChartsRuntime };
})(window);
