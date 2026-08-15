-- Provision all Phanes schemas. Runs once on first boot of a fresh volume.
-- (phanes_task and the phanes user are created by the container's own env
-- vars; this covers everything else.)
CREATE DATABASE IF NOT EXISTS phanes_agent CHARACTER SET utf8mb4;
GRANT ALL PRIVILEGES ON phanes_agent.* TO 'phanes'@'%';
FLUSH PRIVILEGES;
