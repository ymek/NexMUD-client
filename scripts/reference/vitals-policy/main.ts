export function activate(): void {
  nex.events.on("character.vitalsChanged", async event => {
    if (event.health.percent === null || event.health.percent >= 25) {
      return;
    }

    nex.log.warn("Health threshold reached", {
      healthPercent: event.health.percent
    });

    await nex.commands.send("flee");
  });
}
