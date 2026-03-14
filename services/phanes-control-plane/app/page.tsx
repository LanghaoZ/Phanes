import styles from "./page.module.css";

export default function Home() {
  return (
    <div className={styles.page}>
      <main className={styles.main}>
        <p className={styles.eyebrow}>Internal Console</p>
        <div className={styles.intro}>
          <h1>Phanes Control Plane</h1>
          <p>
            A lightweight internal UI for monitoring tasks, agent workflows,
            sandbox sessions, and future system management APIs.
          </p>
        </div>
        <div className={styles.panels}>
          <section className={styles.panel}>
            <span className={styles.label}>Tasks</span>
            <strong>Pending API integration</strong>
            <p>Track task execution, status, retries, and actor activity.</p>
          </section>
          <section className={styles.panel}>
            <span className={styles.label}>Agent Workflows</span>
            <strong>Planned</strong>
            <p>Inspect orchestration state, routing decisions, and outputs.</p>
          </section>
          <section className={styles.panel}>
            <span className={styles.label}>Sandbox Sessions</span>
            <strong>Planned</strong>
            <p>Review execution runs, artifacts, and resource usage.</p>
          </section>
        </div>
      </main>
    </div>
  );
}
